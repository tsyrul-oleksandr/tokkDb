using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Retention;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// NF-4d and NF-4d1 through the assistant (step 9.4): an erasure is a card that says it cannot
/// be undone and that conversations are kept; right after it commits - no compaction, no close -
/// a distinctive value is in neither the database file nor its journal except where the person
/// typed it; and right after a per-request purge commits, a value that existed only in the purged
/// versions is in neither file.
/// </summary>
public sealed class ErasureThroughAssistantTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("erasure-assistant");

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task An_erasure_through_the_assistant_leaves_the_value_only_where_the_person_typed_it()
    {
        const string distinctive = "ZEBRA-LANTERN-9911";
        using var host = new FileHost(_database.FilePath);
        var record = host.Storage.Create("conferences", new Dictionary<string, object?> { ["name"] = "PyCon Lviv", ["city"] = "Lviv", ["date"] = new DateOnly(2025, 3, 14), ["cost"] = 12000m, ["notes"] = distinctive });
        host.Storage.Update(record.With("cost", 12500m));
        Assert.True(Contains(_database.FilePath, distinctive) || Contains(Journal, distinctive), "the value should be on disk before the erase");

        // Asked for, as the browser or a "for good" removal asks: the card says what NF-4d1 asks.
        var asked = await host.Orchestrator.ChangeAsync(null, new EraseRecords("conferences", [record.Id]), "Erase PyCon Lviv for good");
        Assert.True(asked.IsWaiting, asked.Reply);
        Assert.NotNull(asked.Question);
        Assert.False(asked.Question.CanBeUndone);
        Assert.Contains("cannot be undone", asked.Question.UndoNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conversations", asked.Question.UndoNote);
        Assert.Contains("gone for good", string.Join(" ", asked.Question.Lines));

        var done = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: true);
        Assert.True(done.Succeeded, done.Reply);
        Assert.Contains("Erased one of conferences for good", done.Reply);
        Assert.Contains("conversations is kept", done.Reply);

        // Gone: the record, every version, and the payloads of the requests that named it.
        Assert.Null(host.Storage.GetById("conferences", record.Id));
        Assert.Null(host.Storage.HeadVersion("conferences", record.Id));
        var change = Assert.Single(host.Recorder.Changes(done.RequestId));
        Assert.Equal(ChangeKind.Delete, change.Kind);
        Assert.Equal(record.Id, change.RecordId);
        Assert.All(host.Recorder.Read(done.RequestId)!.Value.Steps.Where(static step => step.Name is "the change" or "what it would do"), step => { Assert.Null(step.Input); Assert.Null(step.Output); });

        // Right after the commit, the raw file and its journal hold the value nowhere but in what the person typed - which was not the value.
        host.Storage.Dispose();
        Assert.True(Occurrences(_database.FilePath, distinctive) == 0, "still in the database file: " + Around(_database.FilePath, distinctive));
        Assert.True(Occurrences(Journal, distinctive) == 0, "still in the journal: " + Around(Journal, distinctive));
        Assert.True(Contains(_database.FilePath, "Erase PyCon Lviv for good"), "the person's own words stay in their conversation");
    }

    [Fact]
    public async Task A_per_request_purge_leaves_a_value_that_existed_only_in_the_purged_versions_in_neither_file()
    {
        const string distinctive = "QUOKKA-VIOLET-3377";
        using var host = new FileHost(_database.FilePath);
        var record = host.Storage.Create("conferences", new Dictionary<string, object?> { ["name"] = "KyivJS", ["city"] = "Kyiv", ["date"] = new DateOnly(2025, 5, 20), ["cost"] = 8500m, ["notes"] = distinctive });

        // A change through the assistant overwrites the value; the old version still holds it.
        var changed = await host.Orchestrator.ChangeAsync(null, new ChangeRecord("conferences", record.Id, "notes", "late registration"), "Change the notes of KyivJS to late registration");
        Assert.True(changed.Succeeded, changed.Reply);
        var change = Assert.Single(host.Recorder.Changes(changed.RequestId));
        Assert.True(host.Storage.Keeps("conferences", record.Id, change.PreviousVersionId!.Value));
        Assert.True(Contains(_database.FilePath, distinctive) || Contains(Journal, distinctive));

        var purged = RetentionSweep.EndWindow(host.Storage, host.Recorder, changed.RequestId, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

        Assert.True(purged > 0);
        Assert.False(host.Storage.Keeps("conferences", record.Id, change.PreviousVersionId.Value));
        Assert.Equal("late registration", host.Storage.GetById("conferences", record.Id)!["notes"]);

        host.Storage.Dispose();
        Assert.True(Occurrences(_database.FilePath, distinctive) == 0, "still in the database file: " + Around(_database.FilePath, distinctive));
        Assert.True(Occurrences(Journal, distinctive) == 0, "still in the journal: " + Around(Journal, distinctive));
    }

    [Fact]
    public async Task Saying_for_good_erases_rather_than_removes()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);
        var stored = await host.Say(null, "", AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv));
        host.Model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);
        await host.Say(stored.ConversationId, "How much did I spend on conferences last year?");

        host.Model.Answer("intent", Scenarios.Intent("remove"));
        var asked = await host.Say(stored.ConversationId, "Erase the Lviv one for good");

        Assert.True(asked.IsWaiting, asked.Reply);
        Assert.StartsWith("Erase one of conferences for good", asked.Question!.Title);
        Assert.False(asked.Question.CanBeUndone);
        var (action, _) = IntentPayloads.ReadStructure(host.Recorder.Request(asked.RequestId)!.Intent!.Payload);
        Assert.IsType<EraseRecords>(action);

        var done = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: true);
        Assert.True(done.Succeeded, done.Reply);
        Assert.DoesNotContain(host.Storage.GetAll("conferences"), static record => record["name"] is "PyCon Lviv");
    }

    /// <summary>The bytes around the first copy, as text, for the message of a failed search.</summary>
    private static string Around(string path, string needle)
    {
        if (!File.Exists(path)) return "(no file)";
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle));
        if (at < 0) return "(none)";
        var from = Math.Max(0, at - 100);
        var to = Math.Min(bytes.Length, at + needle.Length + 100);
        return Encoding.UTF8.GetString(bytes, from, to - from).Replace('\0', '.');
    }

    private string Journal => _database.FilePath + ".wal";

    private static bool Contains(string path, string needle) => Occurrences(path, needle) > 0;

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

    /// <summary>The application over a real file with the scripted model: the same wiring the app's composition root does.</summary>
    private sealed class FileHost : IDisposable
    {
        public FileHost(string path)
        {
            Storage = new TokkDbStorage(path);
            Scenarios.GivenConferences(Storage);
            Storage.AddColumn("conferences", new ColumnDefinition("notes", ColumnType.Text));
            Model = new ScriptedModel();
            Services = new ServiceCollection().AddScriptedModel(Model).AddAssistantAgents().AddSingleton(Storage.Traces).AddSingleton(new ToolCatalog(Storage)).BuildServiceProvider();
            Orchestrator = new Orchestrator(Storage, Storage.Traces, Services.GetRequiredService<OperationRunner>(), new ToolCatalog(Storage));
        }

        public TokkDbStorage Storage { get; }
        public ScriptedModel Model { get; }
        public ServiceProvider Services { get; }
        public Orchestrator Orchestrator { get; }
        public ITraceRecorder Recorder => Storage.Traces;

        public void Dispose()
        {
            Services.Dispose();
            Storage.Dispose();
        }
    }
}
