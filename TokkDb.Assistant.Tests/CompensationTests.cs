using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// AJ-4, AJ-5, AJ-8, V-7, V-18 and N-14 of the versioning plan; AG-11 to AG-11f of the assistant
/// plan: compensation of one request as two passes inside one unit of work, over the contract
/// only, so that it means the same over both backends. Each backend runs the whole of it.
/// </summary>
public abstract class CompensationTests : IDisposable
{
    private const string Conferences = "conferences";
    private const string Expenses = "expenses";
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

    /// <summary>
    /// What the orchestrator will do around every write: record the change with the versions the
    /// write replaced and produced, and the reversibility its collection declares (AJ-5, TR-4).
    /// </summary>
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

        public StorageRecord Update(StorageRecord record)
        {
            var before = storage.HeadVersion(record.CollectionName, record.Id)!.Value;
            Assert.True(storage.Update(record));
            Changes.Add(DataChanges.Update(Id, record.CollectionName, record.Id, before,
                storage.HeadVersion(record.CollectionName, record.Id)!.Value, Compensation.ReversibilityOf(storage, record.CollectionName)));
            return storage.GetById(record.CollectionName, record.Id)!;
        }

        public void Delete(string collection, Ulid id)
        {
            var effect = storage.InspectDelete(collection, id);
            var heads = effect.WouldAlsoBeRemoved.ToDictionary(
                static reference => reference,
                reference => storage.HeadVersion(reference.CollectionName, reference.Id)!.Value);
            var before = storage.HeadVersion(collection, id)!.Value;

            var result = storage.Delete(collection, id);

            // Each cascaded deletion is its own change (SC-8a), recorded before the record they
            // went with, which is the order the storage removed them in.
            foreach (var removed in result.AlsoRemoved)
            {
                Changes.Add(DataChanges.Delete(Id, removed.CollectionName, removed.Id, heads[removed],
                    storage.HeadVersion(removed.CollectionName, removed.Id)!.Value, Compensation.ReversibilityOf(storage, removed.CollectionName)));
            }

            Changes.Add(DataChanges.Delete(Id, collection, id, before, storage.HeadVersion(collection, id)!.Value,
                Compensation.ReversibilityOf(storage, collection)));
        }
    }

    private static void GivenConferencesAndExpenses(IStorage storage, RelationIntegrity integrity = RelationIntegrity.Restrict)
    {
        storage.CreateCollection(new CollectionDefinition(Conferences, "conferences I went to", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, "what it was called", required: true, unique: true),
            new ColumnDefinition("city", ColumnType.Text, "where it was")
        ]));
        storage.CreateCollection(new CollectionDefinition(Expenses, "money I spent", columns:
        [
            new ColumnDefinition("what", ColumnType.Text, "what for", required: true),
            new ColumnDefinition("conference", ColumnType.Text, "which conference"),
            new ColumnDefinition("amount_eur", ColumnType.Decimal, "how much")
        ]));
        storage.AddRelation(new RelationDefinition("expense_conference", Expenses, "conference", Conferences, "name", integrity, "the conference"));
    }

    private static void GivenNotes(IStorage storage)
    {
        storage.CreateCollection(new CollectionDefinition(Notes, "things I wrote down", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is about", required: true),
            new ColumnDefinition("body", ColumnType.Text, "what it says")
        ]));
    }

    private static Dictionary<string, object?> Conference(string name, string city = "Prague") => new() { ["name"] = name, ["city"] = city };
    private static Dictionary<string, object?> Expense(string what, string conference, decimal amount = 10m) => new() { ["what"] = what, ["conference"] = conference, ["amount_eur"] = amount };
    private static Dictionary<string, object?> Note(string title, string body = "b") => new() { ["title"] = title, ["body"] = body };

    // ---- What is undone ---------------------------------------------------------------------------

    /// <summary>A request that updated one record twice is undone, although the first restore creates a new version.</summary>
    [Fact]
    public void A_request_that_updated_a_record_twice_is_undone()
    {
        var storage = Open();
        GivenNotes(storage);
        var note = storage.Create(Notes, Note("first"));
        var request = new Request(storage);
        var once = request.Update(note.With("title", "second"));
        request.Update(once.With("title", "third"));

        Compensation.Undo(storage, request.Changes);

        Assert.Equal("first", storage.GetById(Notes, note.Id)!["title"]);
        // A collection with neither a unique column nor a relation: every change was Reversible.
        Assert.All(request.Changes, change => Assert.Equal(Reversibility.Reversible, change.Reversibility));
    }

    /// <summary>X a→b, Y c→a, X b→c on a unique column is undone: the end state has no collision, and the replay in reverse order passes through none.</summary>
    [Fact]
    public void A_swap_through_a_unique_column_is_undone()
    {
        var storage = Open();
        GivenConferencesAndExpenses(storage);
        var x = storage.Create(Conferences, Conference("a"));
        var y = storage.Create(Conferences, Conference("c"));
        var request = new Request(storage);
        x = request.Update(x.With("name", "b"));
        request.Update(y.With("name", "a"));
        request.Update(x.With("name", "c"));

        Compensation.Undo(storage, request.Changes);

        Assert.Equal("a", storage.GetById(Conferences, x.Id)!["name"]);
        Assert.Equal("c", storage.GetById(Conferences, y.Id)!["name"]);
        Assert.All(request.Changes, change => Assert.Equal(Reversibility.ReversibleWithConditions, change.Reversibility));
    }

    /// <summary>Undoing an import after an unrelated record changed still works, and removes exactly what the import added.</summary>
    [Fact]
    public void Undoing_an_import_after_an_unrelated_change_removes_exactly_what_it_added()
    {
        var storage = Open();
        GivenNotes(storage);
        var older = storage.Create(Notes, Note("older"));
        var request = new Request(storage);
        var imported = Enumerable.Range(0, 20).Select(i => request.Create(Notes, Note($"row {i}")).Id).ToList();
        storage.Update(older.With("title", "older, edited"));

        var undone = Compensation.Undo(storage, request.Changes);

        Assert.Equal(20, undone.Count);
        Assert.Equal([older.Id], storage.GetAll(Notes).Select(static note => note.Id));
        Assert.Equal("older, edited", storage.GetById(Notes, older.Id)!["title"]);
        Assert.All(imported, id => Assert.Null(storage.GetById(Notes, id)));
    }

    /// <summary>Undoing a cascade deletion replays in exact reverse order: the conference before the expenses that point at it.</summary>
    [Fact]
    public void Undoing_a_cascade_deletion_restores_the_conference_before_its_expenses()
    {
        var storage = Open();
        GivenConferencesAndExpenses(storage, RelationIntegrity.Cascade);
        var conference = storage.Create(Conferences, Conference("EuroPython"));
        var expenses = Enumerable.Range(0, 4).Select(i => storage.Create(Expenses, Expense($"expense {i}", "EuroPython")).Id).ToList();
        var request = new Request(storage);
        request.Delete(Conferences, conference.Id);
        Assert.Equal(5, request.Changes.Count);
        Assert.Equal(Conferences, request.Changes[^1].CollectionName);
        Assert.Empty(storage.GetAll(Expenses));

        Compensation.Undo(storage, request.Changes);

        Assert.Equal("EuroPython", storage.GetById(Conferences, conference.Id)!["name"]);
        Assert.Equal(expenses.Order(), storage.GetAll(Expenses).Select(static expense => expense.Id).Order());
    }

    // ---- What blocks, found before anything is written --------------------------------------------

    /// <summary>A record changed A→B→A by a later request counts as touched, which a content hash could not tell.</summary>
    [Fact]
    public void A_record_changed_and_changed_back_by_a_later_request_blocks_the_undo()
    {
        var storage = Open();
        GivenNotes(storage);
        var note = storage.Create(Notes, Note("A"));
        var request = new Request(storage);
        var changed = request.Update(note.With("title", "B"));
        var later = new Request(storage);
        var back = later.Update(changed.With("title", "C"));
        later.Update(back.With("title", "B"));

        var refusal = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, request.Changes));

        var blocker = Assert.Single(refusal.Blockers);
        Assert.Equal(new RecordReference(Notes, note.Id), blocker.Record);
        Assert.Contains("touched since", blocker.Reason);
        Assert.False(refusal.AtReplay);
        Assert.Equal("B", storage.GetById(Notes, note.Id)!["title"]);
    }

    /// <summary>An undo blocked by one touched record and one unique collision is refused before any write, naming both.</summary>
    [Fact]
    public void Two_blockers_are_both_named_and_nothing_is_written()
    {
        var storage = Open();
        GivenConferencesAndExpenses(storage);
        var touched = storage.Create(Conferences, Conference("one"));
        var collided = storage.Create(Conferences, Conference("x"));
        var request = new Request(storage);
        touched = request.Update(touched.With("city", "Brno"));
        request.Update(collided.With("name", "y"));
        storage.Update(touched.With("city", "Wien"));
        var taker = storage.Create(Conferences, Conference("x", "Bern"));
        var heads = new[] { touched.Id, collided.Id, taker.Id }.Select(id => storage.HeadVersion(Conferences, id)).ToList();

        var refusal = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, request.Changes));

        Assert.Equal(2, refusal.Blockers.Count);
        Assert.Contains(refusal.Blockers, blocker => blocker.Record.Id == touched.Id && blocker.Reason.Contains("touched since"));
        Assert.Contains(refusal.Blockers, blocker => blocker.Record.Id == collided.Id && blocker.Other == new RecordReference(Conferences, taker.Id));
        Assert.Equal(heads, new[] { touched.Id, collided.Id, taker.Id }.Select(id => storage.HeadVersion(Conferences, id)));
    }

    /// <summary>
    /// The three counterexamples of V-18, each refused whole naming what blocks it: a value
    /// another record took since, a reference to a record deleted since, and an insert whose
    /// undo would delete a record something now refers to.
    /// </summary>
    [Fact]
    public void The_three_counterexamples_of_V18_refuse_whole_and_name_the_blocker()
    {
        var storage = Open();
        GivenConferencesAndExpenses(storage);

        // A's name x→y, then B takes x.
        var a = storage.Create(Conferences, Conference("x"));
        var first = new Request(storage);
        first.Update(a.With("name", "y"));
        var b = storage.Create(Conferences, Conference("x"));
        var one = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, first.Changes));
        Assert.Equal(new RecordReference(Conferences, b.Id), Assert.Single(one.Blockers).Other);
        Assert.Equal("y", storage.GetById(Conferences, a.Id)!["name"]);

        // An expense's conference c1→c2, then c1 is deleted.
        var c1 = storage.Create(Conferences, Conference("c1"));
        storage.Create(Conferences, Conference("c2"));
        var expense = storage.Create(Expenses, Expense("tickets", "c1"));
        var second = new Request(storage);
        second.Update(expense.With("conference", "c2"));
        storage.Delete(Conferences, c1.Id);
        var two = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, second.Changes));
        Assert.Contains("nothing in 'conferences' holds", Assert.Single(two.Blockers).Reason);
        Assert.Equal("c2", storage.GetById(Expenses, expense.Id)!["conference"]);

        // An import creates a conference that a later expense refers to.
        var third = new Request(storage);
        var imported = third.Create(Conferences, Conference("c3"));
        var referrer = storage.Create(Expenses, Expense("hotel", "c3"));
        var three = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, third.Changes));
        var blocker = Assert.Single(three.Blockers);
        Assert.Equal(new RecordReference(Conferences, imported.Id), blocker.Record);
        Assert.Equal(new RecordReference(Expenses, referrer.Id), blocker.Other);
        Assert.NotNull(storage.GetById(Conferences, imported.Id));
    }

    /// <summary>
    /// The limit AG-11a states: validation checks the end state, the replay passes through
    /// intermediate ones. a→b→c in the request, b taken since by an outside record: the end
    /// state a is fine, the replay's b is not, and the refusal is atomic and names that record.
    /// </summary>
    [Fact]
    public void An_intermediate_state_taken_since_is_refused_at_replay_atomically_naming_the_record()
    {
        var storage = Open();
        GivenConferencesAndExpenses(storage);
        var x = storage.Create(Conferences, Conference("a"));
        var request = new Request(storage);
        x = request.Update(x.With("name", "b"));
        request.Update(x.With("name", "c"));
        var outsider = storage.Create(Conferences, Conference("b"));
        var head = storage.HeadVersion(Conferences, x.Id);

        Assert.Empty(Compensation.Validate(storage, request.Changes));
        var refusal = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, request.Changes));

        Assert.True(refusal.AtReplay);
        var blocker = Assert.Single(refusal.Blockers);
        Assert.Equal(new RecordReference(Conferences, x.Id), blocker.Record);
        Assert.Equal(new RecordReference(Conferences, outsider.Id), blocker.Other);
        // Storage unchanged: the unit of work was rolled back whole.
        Assert.Equal("c", storage.GetById(Conferences, x.Id)!["name"]);
        Assert.Equal(head, storage.HeadVersion(Conferences, x.Id));
    }

    /// <summary>
    /// AG-11b: the rest is validated as an undo of its own. Leaving out the record whose restore
    /// would have released a unique value reports the collision this creates for another record
    /// in the subset, instead of offering a partial undo that would fail.
    /// </summary>
    [Fact]
    public void A_subset_that_leaves_out_the_releasing_record_reports_the_collision_it_creates()
    {
        var storage = Open();
        GivenConferencesAndExpenses(storage);
        var x = storage.Create(Conferences, Conference("a"));
        var y = storage.Create(Conferences, Conference("c"));
        var request = new Request(storage);
        x = request.Update(x.With("name", "b"));
        request.Update(y.With("name", "a"));
        request.Update(x.With("name", "c"));

        // The whole request is fine; the rest without X is not: Y would go back to a, which X holds.
        Assert.Empty(Compensation.Validate(storage, request.Changes));
        var blockers = Compensation.Validate(storage, request.Changes.Where(change => change.RecordId == y.Id).ToList());

        var blocker = Assert.Single(blockers);
        Assert.Equal(new RecordReference(Conferences, y.Id), blocker.Record);
        Assert.Equal(new RecordReference(Conferences, x.Id), blocker.Other);
        Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, request.Changes, change => change.RecordId == y.Id));
        Assert.Equal("a", storage.GetById(Conferences, y.Id)!["name"]);
    }

    /// <summary>
    /// AJ-5: on a collection with neither a unique column nor a relation, undo never refuses
    /// unless the record was touched or its version purged - and a purged version makes the
    /// change not reversible, named as such.
    /// </summary>
    [Fact]
    public void A_purged_version_makes_the_change_not_reversible()
    {
        var storage = Open();
        GivenNotes(storage);
        var note = storage.Create(Notes, Note("first"));
        var request = new Request(storage);
        request.Update(note.With("title", "second"));
        Assert.Equal(Reversibility.Reversible, request.Changes[0].Reversibility);

        storage.PurgeRecordHistory(Notes, note.Id, DateTimeOffset.UtcNow.AddSeconds(1));

        var refusal = Assert.Throws<CompensationRefusedException>(() => Compensation.Undo(storage, request.Changes));
        var blocker = Assert.Single(refusal.Blockers);
        Assert.Contains("not reversible", blocker.Reason);
        Assert.Contains("no longer kept", blocker.Reason);
        Assert.Equal("second", storage.GetById(Notes, note.Id)!["title"]);
    }

    /// <summary>AJ-8: a configuration that would purge history inside the compensation window is refused, naming both moments.</summary>
    [Fact]
    public void A_configuration_that_purges_inside_the_compensation_window_is_refused()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var windows = RetentionWindows.Default with { Compensation = TimeSpan.FromDays(90), History = TimeSpan.FromDays(30) };

        var refusal = Assert.Throws<RetentionConflictException>(() => windows.PurgeHistoryBefore(now));

        Assert.Equal(now.AddDays(-30), refusal.PurgeHistoryBefore);
        Assert.Equal(now.AddDays(-90), refusal.CompensationWindowStart);
        Assert.Contains(refusal.PurgeHistoryBefore.ToString("O"), refusal.Message);
        Assert.Contains(refusal.CompensationWindowStart.ToString("O"), refusal.Message);
        Assert.Equal(now.AddDays(-90), RetentionWindows.Default.PurgeHistoryBefore(now));
    }
}

public sealed class MemoryStorageCompensationTests : CompensationTests
{
    protected override IStorage NewStorage() => new MemoryStorage();
}

public sealed class TokkDbStorageCompensationTests : CompensationTests
{
    private readonly List<TemporaryDatabase> _databases = [];

    protected override IStorage NewStorage()
    {
        var database = new TemporaryDatabase("compensation");
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
