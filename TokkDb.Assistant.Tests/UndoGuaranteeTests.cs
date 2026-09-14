using System.Text;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// G-10 of the versioning plan, through <see cref="IStorage"/> and the compensation over it
/// only: <b>undo returns to exactly before, or refuses whole</b>. After a request is
/// compensated, every record it changed reads as it did before the request; if any record
/// cannot, nothing changes, and every record that blocks the undo is named. With it, the
/// scenarios S-8 and S-10, which both backends run, and the parts of S-10 only a file can show.
/// </summary>
public abstract class UndoGuaranteeTests : IDisposable
{
    private const string Papers = "papers";
    private const string Notes = "notes";

    private readonly List<IDisposable> _opened = [];

    protected abstract IStorage NewStorage();

    protected IStorage Open()
    {
        var storage = NewStorage();
        if (storage is IDisposable disposable) _opened.Add(disposable);
        return storage;
    }

    public virtual void Dispose()
    {
        foreach (var opened in _opened) opened.Dispose();
        _opened.Clear();
        GC.SuppressFinalize(this);
    }

    private static void GivenPapers(IStorage storage)
    {
        storage.CreateCollection(new CollectionDefinition(Papers, "papers I have read", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is called", required: true),
            new ColumnDefinition("doi", ColumnType.Text, "its identifier", unique: true),
            new ColumnDefinition("year", ColumnType.Integer, "when it came out")
        ]));
    }

    private static void GivenNotes(IStorage storage)
    {
        storage.CreateCollection(new CollectionDefinition(Notes, "things I wrote down", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is about", required: true),
            new ColumnDefinition("body", ColumnType.Text, "what it says")
        ]));
    }

    private static Dictionary<string, object?> Paper(int n, int year = 2020) => new()
    {
        ["title"] = $"paper {n}", ["doi"] = $"10.1000/{n}", ["year"] = (long)year
    };

    /// <summary>A request as the orchestrator will record it: every write with the versions it replaced and produced.</summary>
    private sealed class Request(IStorage storage)
    {
        public Ulid Id { get; } = Ulid.NewUlid();
        public List<DataChange> Changes { get; } = [];

        public StorageRecord Create(string collection, Dictionary<string, object?> fields)
        {
            var record = storage.Create(collection, fields);
            Changes.Add(DataChanges.Insert(Id, collection, record.Id, storage.HeadVersion(collection, record.Id)!.Value,
                Compensation.ReversibilityOf(storage, collection)));
            return record;
        }

        public void Update(StorageRecord record)
        {
            var before = storage.HeadVersion(record.CollectionName, record.Id)!.Value;
            Assert.True(storage.Update(record));
            Changes.Add(DataChanges.Update(Id, record.CollectionName, record.Id, before,
                storage.HeadVersion(record.CollectionName, record.Id)!.Value, Compensation.ReversibilityOf(storage, record.CollectionName)));
        }

        public void Delete(string collection, Ulid id)
        {
            var before = storage.HeadVersion(collection, id)!.Value;
            storage.Delete(collection, id);
            Changes.Add(DataChanges.Delete(Id, collection, id, before, storage.HeadVersion(collection, id)!.Value,
                Compensation.ReversibilityOf(storage, collection)));
        }
    }

    private static string Snapshot(IStorage storage, string collection, Ulid id)
    {
        var record = storage.GetById(collection, id);
        return record is null
            ? "gone"
            : string.Join("|", record.Fields.OrderBy(static field => field.Key, StringComparer.Ordinal).Select(field => $"{field.Key}={field.Value}"));
    }

    // ---- G-10 --------------------------------------------------------------------------------

    /// <summary>
    /// Generated requests over a collection with a unique column: whatever a request did -
    /// created, changed, changed again, deleted - undoing it leaves every record it touched
    /// reading exactly as before it, and every other record as it was.
    /// </summary>
    [Fact]
    public void After_a_request_is_compensated_every_record_it_changed_reads_as_before()
    {
        var storage = Open();
        GivenPapers(storage);
        var random = new Random(910);
        var records = Enumerable.Range(0, 30).Select(i => storage.Create(Papers, Paper(i)).Id).ToList();
        var next = 30;

        for (var round = 0; round < 12; round++)
        {
            var before = records.ToDictionary(id => id, id => Snapshot(storage, Papers, id));
            var request = new Request(storage);
            var touched = new HashSet<Ulid>();

            for (var step = 0; step < 8; step++)
            {
                var roll = random.Next(10);
                if (roll < 2)
                {
                    var created = request.Create(Papers, Paper(next++, 2000 + step));
                    records.Add(created.Id);
                    touched.Add(created.Id);
                }
                else if (roll < 8)
                {
                    var id = records[random.Next(records.Count)];
                    var current = storage.GetById(Papers, id);
                    if (current is null) continue;
                    request.Update(random.Next(3) == 0
                        ? current.With("doi", $"10.1000/{next++}")
                        : current.With("title", $"paper {id} v{step}").With("year", (long)(2000 + random.Next(30))));
                    touched.Add(id);
                }
                else
                {
                    var id = records[random.Next(records.Count)];
                    if (storage.GetById(Papers, id) is null) continue;
                    request.Delete(Papers, id);
                    touched.Add(id);
                }
            }

            Compensation.Undo(storage, request.Changes);

            foreach (var id in records)
            {
                var expected = before.TryGetValue(id, out var snapshot) ? snapshot : "gone";
                Assert.Equal(expected, Snapshot(storage, Papers, id));
            }
            Assert.NotEmpty(touched);
        }
    }

    /// <summary>
    /// If any record cannot be returned, nothing changes and every record that blocks the undo
    /// is named: here one record touched since, and one whose value another record took.
    /// </summary>
    [Fact]
    public void If_any_record_cannot_be_returned_nothing_changes_and_every_blocker_is_named()
    {
        var storage = Open();
        GivenPapers(storage);
        var a = storage.Create(Papers, Paper(1));
        var b = storage.Create(Papers, Paper(2));
        var c = storage.Create(Papers, Paper(3));
        var request = new Request(storage);
        request.Update(a.With("title", "a, by the request"));
        request.Update(b.With("doi", "10.1000/2b"));
        request.Update(c.With("year", 2021L));
        storage.Update(storage.GetById(Papers, a.Id)!.With("title", "a, by somebody else"));
        var fourth = Paper(4);
        fourth["doi"] = "10.1000/2";
        var taker = storage.Create(Papers, fourth);
        var before = new[] { a.Id, b.Id, c.Id, taker.Id }.ToDictionary(id => id, id => (Snapshot(storage, Papers, id), storage.HeadVersion(Papers, id)));

        var refusal = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, request.Changes));

        Assert.Equal([a.Id, b.Id], refusal.Blockers.Select(static blocker => blocker.Record.Id).Order());
        Assert.Contains(refusal.Blockers, blocker => blocker.Other == new RecordReference(Papers, taker.Id));
        foreach (var (id, (snapshot, head)) in before)
        {
            Assert.Equal(snapshot, Snapshot(storage, Papers, id));
            Assert.Equal(head, storage.HeadVersion(Papers, id));
        }
    }

    // ---- S-8 ---------------------------------------------------------------------------------

    /// <summary>
    /// S-8: the assistant deletes a record too wide for any journal cap, and undoing the request
    /// brings back every field. In a second run, an A→B→A edit by a later request blocks the
    /// undo, and the record is named.
    /// </summary>
    [Fact]
    public void S8_undo_from_history()
    {
        var storage = Open();
        GivenNotes(storage);
        var body = new string('n', 2 * PayloadLimits.Default.LongestPayload);
        var note = storage.Create(Notes, new Dictionary<string, object?> { ["title"] = "wide", ["body"] = body });
        var request = new Request(storage);
        request.Delete(Notes, note.Id);
        Assert.Null(storage.GetById(Notes, note.Id));
        Assert.Equal(Reversibility.Reversible, request.Changes[0].Reversibility);

        Compensation.Undo(storage, request.Changes);

        var back = storage.GetById(Notes, note.Id);
        Assert.NotNull(back);
        Assert.Equal("wide", back["title"]);
        Assert.Equal(body, back["body"]);

        // The second run: A → B by the request, B → C → B by a later one.
        var edited = new Request(storage);
        edited.Update(back.With("title", "B"));
        var later = new Request(storage);
        later.Update(storage.GetById(Notes, note.Id)!.With("title", "C"));
        later.Update(storage.GetById(Notes, note.Id)!.With("title", "B"));

        var refusal = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, edited.Changes));

        Assert.Equal(new RecordReference(Notes, note.Id), Assert.Single(refusal.Blockers).Record);
        Assert.Equal("B", storage.GetById(Notes, note.Id)!["title"]);
    }

    // ---- S-10 --------------------------------------------------------------------------------

    /// <summary>
    /// S-10: an import of 10 000 rows in one unit of work is one request whose changes are of
    /// fixed size whatever the width of the rows, and undoing it removes exactly the imported
    /// records. (That the engine shows one operation for all of them is checked on the file.)
    /// </summary>
    [Fact]
    public void S10_an_import_is_one_request_and_its_undo_removes_exactly_what_it_added()
    {
        var storage = Open();
        GivenNotes(storage);
        var kept = storage.Create(Notes, new Dictionary<string, object?> { ["title"] = "kept", ["body"] = "here before" });
        var request = new Request(storage);

        var imported = storage.InUnitOfWork(() =>
            Enumerable.Range(0, 10_000).Select(i => request.Create(Notes, new Dictionary<string, object?>
            {
                ["title"] = $"row {i}", ["body"] = new string((char)('a' + i % 26), 20 + i % 200)
            }).Id).ToList());

        Assert.Equal(10_000, request.Changes.Count);
        Assert.All(request.Changes, change => Assert.Equal(0, change.Weight));
        Assert.Equal(10_001, storage.GetAll(Notes).Count);

        var undone = Compensation.Undo(storage, request.Changes);

        Assert.Equal(10_000, undone.Count);
        Assert.Equal([kept.Id], storage.GetAll(Notes).Select(static note => note.Id));
        Assert.All(imported, id => Assert.Null(storage.GetById(Notes, id)));
    }
}

public sealed class MemoryStorageUndoGuaranteeTests : UndoGuaranteeTests
{
    protected override IStorage NewStorage() => new MemoryStorage();
}

public sealed class TokkDbStorageUndoGuaranteeTests : UndoGuaranteeTests
{
    private readonly List<TemporaryDatabase> _databases = [];

    protected override IStorage NewStorage()
    {
        var database = new TemporaryDatabase("undo");
        _databases.Add(database);
        return new TokkDbStorage(database.FilePath);
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var database in _databases) database.Dispose();
        _databases.Clear();
    }
}

/// <summary>
/// What only the file can show: S-7's erase through the assistant, S-10's one operation and
/// fixed-size journal, AJ-6's table, and AJ-7's cleared payloads.
/// </summary>
public sealed class AssistantErasureAndTraceTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("assistant-erasure");

    public void Dispose() => _database.Dispose();

    private static bool Contains(string path, string needle) =>
        File.Exists(path) && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    private static int Occurrences(string path, string needle)
    {
        if (!File.Exists(path)) return 0;
        var bytes = File.ReadAllBytes(path).AsSpan();
        var pattern = Encoding.UTF8.GetBytes(needle);
        var count = 0;
        var at = bytes.IndexOf(pattern);
        while (at >= 0)
        {
            count++;
            bytes = bytes[(at + pattern.Length)..];
            at = bytes.IndexOf(pattern);
        }
        return count;
    }

    /// <summary>
    /// S-7 and AJ-7: after the record carrying a distinctive string is erased through the
    /// assistant, the string is found nowhere in the database file or its journal - except in
    /// the conversation entry the user typed, which the confirmation said would be kept.
    /// </summary>
    [Fact]
    public void S7_erase_through_the_assistant()
    {
        const string secret = "ERASED-Xq7-secret-value";
        var journal = Disk.Journal.GetJournalPath(_database.FilePath);
        using var storage = new TokkDbStorage(_database.FilePath);
        storage.CreateCollection(new CollectionDefinition("notes", "notes", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is about", required: true),
            new ColumnDefinition("body", ColumnType.Text, "what it says")
        ]));
        var conversation = storage.Conversations.Start("about the secret");
        storage.Conversations.Append(conversation.Id, TurnSpeaker.Person, $"please note {secret} for me");
        var request = storage.Traces.Begin(conversation.Id, "store a note");
        var record = storage.InUnitOfWork(() =>
        {
            var note = storage.Create("notes", new Dictionary<string, object?> { ["title"] = secret, ["body"] = $"{secret} body" });
            storage.Traces.Record(DataChanges.Insert(request.Id, "notes", note.Id, storage.HeadVersion("notes", note.Id)!.Value, Reversibility.Reversible));
            return note;
        });
        storage.Update(record.With("body", $"{secret} body, revised"));
        storage.Update(record.With("body", $"{secret} body, revised again"));
        var step = new ExecutionStep(Ulid.NewUlid(), request.Id, "the write", StepStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Input = $"write a note saying {secret}", Output = $"wrote {secret}"
        };
        storage.Traces.Record(step);
        storage.Traces.Move(request.Id, RequestState.Completed, expectedTransitions: 0);
        Assert.True(Occurrences(_database.FilePath, secret) > 3);

        Assert.True(storage.Erase("notes", record.Id));

        // Right after the commit, connection open: once in the file - the conversation turn -
        // and nowhere in the journal.
        Assert.Equal(1, Occurrences(_database.FilePath, secret));
        Assert.False(Contains(journal, secret));
        Assert.Contains(secret, Assert.Single(storage.Conversations.Turns(conversation.Id)).Text);
        var (kept, steps) = storage.Traces.Read(request.Id)!.Value;
        Assert.Equal(RequestState.Completed, kept.State);
        var cleared = Assert.Single(steps);
        Assert.Equal("the write", cleared.Name);
        Assert.Null(cleared.Input);
        Assert.Null(cleared.Output);
        // The change journal keeps the identifier, which carries no value.
        Assert.Equal(record.Id, Assert.Single(storage.Traces.Changes(request.Id)).RecordId);
        Assert.Contains("conversations are kept", Erasure.ConversationsAreKept, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Erasure.ConversationsAreKept, Erasure.Confirmation("this note"));
    }

    /// <summary>S-10 on the file: one operation for all 10 000 rows, and a journal whose size does not depend on the width of the rows.</summary>
    [Fact]
    public void S10_an_import_is_one_operation_and_its_journal_does_not_grow_with_the_rows()
    {
        var narrow = Import("narrow", 10_000, columns: 2, width: 8);
        var wide = Import("wide", 10_000, columns: 12, width: 200);

        Assert.Equal(1, narrow.Operations);
        Assert.Equal(1, wide.Operations);
        Assert.Equal(narrow.JournalBytes, wide.JournalBytes);
        Assert.True(wide.DataBytes > narrow.DataBytes * 10, "the wide rows should cost far more data");
    }

    private static (int Operations, long JournalBytes, long DataBytes) Import(string name, int rows, int columns, int width)
    {
        using var database = new TemporaryDatabase($"import-{name}");
        var definition = new CollectionDefinition("rows", "an import", columns:
            Enumerable.Range(0, columns).Select(c => new ColumnDefinition($"c{c}", ColumnType.Text, "a column")).ToList());
        List<Ulid> ids;
        using (var storage = new TokkDbStorage(database.FilePath))
        {
            storage.CreateCollection(definition);
            var request = storage.Traces.Begin(Ulid.NewUlid(), "import a file");
            ids = storage.InUnitOfWork(() => Enumerable.Range(0, rows).Select(i =>
            {
                var record = storage.Create("rows", Enumerable.Range(0, columns).ToDictionary(c => $"c{c}", c => (object?)new string((char)('a' + (i + c) % 26), width)));
                storage.Traces.Record(DataChanges.Insert(request.Id, "rows", record.Id, storage.HeadVersion("rows", record.Id)!.Value, Reversibility.Reversible));
                return record.Id;
            }).ToList());
        }

        using var connection = new TokkDbConnection(database.FilePath);
        connection.Load();
        var entities = connection.Entities(new FieldMapSerializerForTests(), "rows");
        var operations = ids.Where((_, i) => i % 500 == 0).Select(id => entities.History(id).Versions.Single().OperationId).Distinct().Count();
        var journalBytes = connection.SystemDocuments.ReadAll(SystemCollections.DataChanges)
            .Sum(entry => (long)Documents.ObjectDocumentUtilities.GetBytesLength(entry.Document));
        var dataBytes = entities.GetAllRecords().Sum(record => (long)record.Value.Values.Sum(value => value is Documents.Values.StringDocumentValue text ? text.Value.Length : 0));
        return (operations, journalBytes, dataBytes);
    }

    /// <summary>AJ-6 and TR-6: the before-and-after table of a record change is read from its two versions, and says so once they are purged.</summary>
    [Fact]
    public void The_before_and_after_table_comes_from_the_versions_and_says_when_they_are_gone()
    {
        using var storage = new TokkDbStorage(_database.FilePath);
        storage.CreateCollection(new CollectionDefinition("notes", "notes", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is about", required: true),
            new ColumnDefinition("body", ColumnType.Text, "what it says")
        ]));
        var note = storage.Create("notes", new Dictionary<string, object?> { ["title"] = "first", ["body"] = "same" });
        var before = storage.HeadVersion("notes", note.Id)!.Value;
        storage.Update(note.With("title", "second"));
        var after = storage.HeadVersion("notes", note.Id)!.Value;
        var change = DataChanges.Update(Ulid.NewUlid(), "notes", note.Id, before, after, Reversibility.Reversible);

        var table = RecordChangeTable.For(storage, change.CollectionName, change.RecordId!.Value, change.PreviousVersionId, change.VersionId);

        Assert.True(table.ValuesAreKept);
        var row = Assert.Single(table.Rows);
        Assert.Equal(("title", (object?)"first", (object?)"second"), (row.ColumnName, row.Before, row.After));
        Assert.Null(table.Note);

        storage.PurgeRecordHistory("notes", note.Id, DateTimeOffset.UtcNow.AddSeconds(1));
        var purged = RecordChangeTable.For(storage, change.CollectionName, change.RecordId!.Value, change.PreviousVersionId, change.VersionId);

        Assert.False(purged.ValuesAreKept);
        Assert.Equal(RecordChangeTable.NoLongerKept, purged.Note);
        Assert.Empty(purged.Rows);
        Assert.Equal(change.RequestId, change.RequestId);
    }
}
