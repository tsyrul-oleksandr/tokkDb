using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// CX-2: conversation history in TokkDb, surviving a restart.
///
/// Every test here closes the database and opens it again, because that is the whole claim.
/// <c>InMemoryConversationHistoryService</c> passes any test that does not.
/// </summary>
public sealed class ConversationHistoryPersistenceTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-conversations-{Ulid.NewUlid()}.db");

    private TokkDbStorage Open() => new(_databaseFilePath);

    //The storage's own history, so the test uses the same instance the application would.
    private static IConversationHistoryService History(TokkDbStorage storage) => storage.Conversations;

    private static ConversationEntry User(string id, string text) =>
        new() { Id = id, Kind = ConversationEntryKind.User, Timestamp = DateTimeOffset.UtcNow, Text = text };

    private static ConversationEntry Assistant(string id, string text) =>
        new() { Id = id, Kind = ConversationEntryKind.Assistant, Timestamp = DateTimeOffset.UtcNow, Text = text };

    [Fact]
    public void AConversationAndItsEventsAreStillThereAfterARestart()
    {
        string conversationId;
        using (var storage = Open())
        {
            var history = History(storage);
            var conversation = history.Create();
            conversationId = conversation.Id;
            history.Append(conversationId, User("m1", "Скільки публікацій за 2024 рік?"));
            history.Append(conversationId, Assistant("m2", "Сімнадцять."));
        }

        using var reopened = Open();
        var reloaded = History(reopened).GetConversation(conversationId);

        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Entries.Count);
        //The first user message named it, and the name came back with it.
        Assert.Equal("Скільки публікацій за 2024 рік?", reloaded.Title);
        Assert.Equal(["m1", "m2"], reloaded.Entries.Select(entry => entry.Id));
        Assert.Equal("Сімнадцять.", reloaded.Entries[1].Text);
        Assert.Equal(ConversationEntryKind.User, reloaded.Entries[0].Kind);
    }

    /// <summary>
    /// The rich payloads an event can carry. They are written as JSON rather than as document
    /// fields, so what has to be checked is that they come back whole.
    /// </summary>
    [Fact]
    public void AToolCallAndAWorkflowStepSurviveWithTheirPayloads()
    {
        string conversationId;
        using (var storage = Open())
        {
            var history = History(storage);
            conversationId = history.Create("Import").Id;
            history.Append(conversationId, new ConversationEntry
            {
                Id = "call-1",
                Kind = ConversationEntryKind.Tool,
                Timestamp = DateTimeOffset.UtcNow,
                Tool = new AgentToolExecution("create_record", AgentToolExecutionStatus.Succeeded, "ok",
                    DateTimeOffset.UtcNow)
                {
                    CallId = "call-1",
                    Arguments = """{"collection":"Article","fields":{"Title":"Проєктування"}}""",
                    Response = """{"id":"01H..."}"""
                }
            });
            history.Append(conversationId, new ConversationEntry
            {
                Id = "wf-1",
                Kind = ConversationEntryKind.Workflow,
                Timestamp = DateTimeOffset.UtcNow,
                Workflow = new ConversationWorkflowEntry("op-1", "Completed", "Imported 17 records")
            });
        }

        using var reopened = Open();
        var reloaded = History(reopened).GetConversation(conversationId)!;

        var tool = Assert.Single(reloaded.Entries, entry => entry.Kind == ConversationEntryKind.Tool).Tool;
        Assert.NotNull(tool);
        Assert.Equal("create_record", tool!.Name);
        Assert.Equal(AgentToolExecutionStatus.Succeeded, tool.Status);
        Assert.Equal("call-1", tool.CallId);
        Assert.Contains("Проєктування", tool.Arguments);

        var workflow = Assert.Single(
            reloaded.Entries, entry => entry.Kind == ConversationEntryKind.Workflow).Workflow;
        Assert.NotNull(workflow);
        Assert.Equal("op-1", workflow!.OperationId);
        Assert.Equal("Imported 17 records", workflow.Message);
    }

    /// <summary>
    /// An event appended again under the same id replaces it in place — a tool call moving from
    /// Started to Completed is one event, not two — and that has to be true of what is stored
    /// as well as of what is in memory.
    /// </summary>
    [Fact]
    public void AnEventAppendedAgainReplacesItInPlaceAcrossARestart()
    {
        string conversationId;
        using (var storage = Open())
        {
            var history = History(storage);
            conversationId = history.Create("Tools").Id;
            history.Append(conversationId, User("m1", "Import the file"));
            history.Append(conversationId, new ConversationEntry
            {
                Id = "call-1",
                Kind = ConversationEntryKind.Tool,
                Timestamp = DateTimeOffset.UtcNow,
                Tool = new AgentToolExecution("import", AgentToolExecutionStatus.Started, null, DateTimeOffset.UtcNow)
            });
            history.Append(conversationId, Assistant("m2", "Working on it"));
            history.Append(conversationId, new ConversationEntry
            {
                Id = "call-1",
                Kind = ConversationEntryKind.Tool,
                Timestamp = DateTimeOffset.UtcNow,
                Tool = new AgentToolExecution("import", AgentToolExecutionStatus.Succeeded, "done",
                    DateTimeOffset.UtcNow)
            });
        }

        using var reopened = Open();
        var reloaded = History(reopened).GetConversation(conversationId)!;

        Assert.Equal(3, reloaded.Entries.Count);
        //Still in the position it was first appended at, not moved to the end.
        Assert.Equal(["m1", "call-1", "m2"], reloaded.Entries.Select(entry => entry.Id));
        Assert.Equal(AgentToolExecutionStatus.Succeeded, reloaded.Entries[1].Tool!.Status);
    }

    [Fact]
    public void ConversationsComeBackMostRecentlyUpdatedFirst()
    {
        using (var storage = Open())
        {
            var history = History(storage);
            var first = history.Create("First").Id;
            var second = history.Create("Second").Id;
            //Touching the first one makes it the most recent.
            history.Append(first, Assistant("m1", "later"));
            Assert.Equal(["First", "Second"], history.GetConversations().Select(item => item.Title));
            Assert.NotEqual(first, second);
        }

        using var reopened = Open();
        Assert.Equal(["First", "Second"],
            History(reopened).GetConversations().Select(conversation => conversation.Title));
    }

    [Fact]
    public void ARenamedConversationKeepsItsNewName()
    {
        string conversationId;
        using (var storage = Open())
        {
            var history = History(storage);
            conversationId = history.Create("Before").Id;
            Assert.True(history.Rename(conversationId, "After"));
            Assert.False(history.Rename("no-such-conversation", "After"));
        }

        using var reopened = Open();
        Assert.Equal("After", History(reopened).GetConversation(conversationId)!.Title);
    }

    [Fact]
    public void ADeletedConversationTakesItsEventsWithIt()
    {
        string deleted;
        string kept;
        using (var storage = Open())
        {
            var history = History(storage);
            deleted = history.Create("Doomed").Id;
            kept = history.Create("Kept").Id;
            history.Append(deleted, User("m1", "one"));
            history.Append(deleted, Assistant("m2", "two"));
            history.Append(kept, User("m3", "three"));

            Assert.True(history.Delete(deleted));
            Assert.False(history.Delete(deleted));
        }

        using var reopened = Open();
        var history2 = History(reopened);
        Assert.Null(history2.GetConversation(deleted));
        var remaining = Assert.Single(history2.GetConversations());
        Assert.Equal(kept, remaining.Id);
        //The deleted conversation's events went with it rather than being reattached here.
        Assert.Equal(["m3"], remaining.Entries.Select(entry => entry.Id));
    }

    /// <summary>
    /// Long enough that the conversation could not have been one document: appending to it has
    /// to stay a write of one event, and the order has to survive whatever the pages do.
    /// </summary>
    [Fact]
    public void ALongConversationKeepsItsOrder()
    {
        string conversationId;
        using (var storage = Open())
        {
            var history = History(storage);
            conversationId = history.Create("Long").Id;
            for (var turn = 0; turn < 400; turn++)
            {
                history.Append(conversationId, turn % 2 == 0
                    ? User($"u{turn}", $"question {turn}")
                    : Assistant($"a{turn}", new string('x', 400)));
            }
        }

        using var reopened = Open();
        var reloaded = History(reopened).GetConversation(conversationId)!;

        Assert.Equal(400, reloaded.Entries.Count);
        Assert.Equal(
            Enumerable.Range(0, 400).Select(turn => turn % 2 == 0 ? $"u{turn}" : $"a{turn}"),
            reloaded.Entries.Select(entry => entry.Id));
        Assert.Equal("question 42", reloaded.Entries[42].Text);
    }

    /// <summary>
    /// Phase 7's exit condition, in one test: everything the application keeps goes into one
    /// database file and is still there when it is opened again.
    /// </summary>
    [Fact]
    public void EverythingTheApplicationKeepsSurvivesARestart()
    {
        Ulid recordId;
        string conversationId;

        using (var storage = Open())
        {
            var history = History(storage);
            new SemanticTypeRegistry(storage.SemanticTypes).Register(new SemanticTypeDefinition(
                "doi", "DOI", "Digital object identifier", Core.ColumnType.String,
                Aliases: ["Doi"], ValidationPatterns: [@"^10\.\d{4,}/.+$"]));

            storage.CreateCollection(new CollectionDefinition(
                "Article",
                "A publication record",
                [
                    new ColumnDefinition("Title", Core.ColumnType.String),
                    new ColumnDefinition("Doi", Core.ColumnType.String, semanticTypeName: "doi", unique: true),
                    new ColumnDefinition("Year", Core.ColumnType.Int32)
                ],
                new Dictionary<string, string?> { ["derivedBy"] = "model" }));
            storage.SetDisplayRule("Article", new DisplayRule("{Title} ({Year})"));

            //One transaction for the import, which is what the batch API is for.
            storage.InBatch(() =>
            {
                for (var index = 0; index < 200; index++)
                {
                    storage.Create("Article", new Dictionary<string, object?>
                    {
                        ["Title"] = $"Article {index}",
                        ["Doi"] = $"10.1000/tokkdb.{index:D4}",
                        ["Year"] = 2000 + index % 25
                    });
                }
            });
            recordId = storage.GetAll("Article").First().Id;

            conversationId = history.Create().Id;
            history.Append(conversationId, User("m1", "Імпортуй публікації"));
            history.Append(conversationId, Assistant("m2", "Готово: 200 записів."));
        }

        using var reopened = Open();

        //Collections and schema.
        var definition = reopened.GetCollectionDefinition("Article");
        Assert.NotNull(definition);
        Assert.Equal("A publication record", definition!.Description);
        Assert.Equal(["Title", "Doi", "Year"], definition.Columns.Select(column => column.Name));
        Assert.Equal("doi", definition.Columns.First(column => column.Name == "Doi").SemanticTypeName);
        //Metadata and display rule.
        Assert.Equal("model", definition.Metadata["derivedBy"]);
        Assert.Equal("{Title} ({Year})", definition.DisplayRule?.Template);
        //Records.
        Assert.Equal(200, reopened.GetAll("Article").Count);
        Assert.NotNull(reopened.GetById("Article", recordId));
        //Semantic types.
        var semanticType = new SemanticTypeRegistry(reopened.SemanticTypes).GetByNameOrAlias("Doi");
        Assert.NotNull(semanticType);
        Assert.Equal("Digital object identifier", semanticType!.Description);
        //Conversations.
        var conversation = History(reopened).GetConversation(conversationId);
        Assert.NotNull(conversation);
        Assert.Equal("Імпортуй публікації", conversation!.Title);
        Assert.Equal(2, conversation.Entries.Count);
    }

    public void Dispose()
    {
        foreach (var path in new[]
                 {
                     _databaseFilePath,
                     TokkDb.Disk.Journal.GetJournalPath(_databaseFilePath),
                     TokkDb.Disk.WriteLock.GetLockPath(_databaseFilePath)
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
