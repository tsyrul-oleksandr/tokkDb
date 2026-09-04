using Microsoft.Extensions.Logging.Abstractions;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

public sealed class ConversationHistoryServiceTests
{
    private static InMemoryConversationHistoryService CreateService() =>
        new(NullLogger<InMemoryConversationHistoryService>.Instance);

    private static ConversationEntry Text(string id, ConversationEntryKind kind, string text, int second = 0) =>
        new()
        {
            Id = id,
            Kind = kind,
            Timestamp = new DateTimeOffset(2026, 9, 2, 12, 0, second, TimeSpan.Zero),
            Text = text
        };

    [Fact]
    public void NewConversationStartsEmptyWithTimestamps()
    {
        var conversation = CreateService().Create();

        Assert.NotEmpty(conversation.Id);
        Assert.Equal(StoredConversation.UntitledConversation, conversation.Title);
        Assert.Empty(conversation.Entries);
        Assert.Equal(conversation.CreatedAt, conversation.UpdatedAt);
    }

    [Fact]
    public void MultipleConversationsAreSupportedAndIsolated()
    {
        var service = CreateService();
        var first = service.Create();
        var second = service.Create();

        service.Append(first.Id, Text("m1", ConversationEntryKind.User, "first conversation"));

        Assert.Equal(2, service.GetConversations().Count);
        Assert.Single(service.GetConversation(first.Id)!.Entries);
        Assert.Empty(service.GetConversation(second.Id)!.Entries);
    }

    [Fact]
    public void EntriesKeepChronologicalOrder()
    {
        var service = CreateService();
        var conversation = service.Create();

        service.Append(conversation.Id, Text("m1", ConversationEntryKind.User, "question", 1));
        service.Append(conversation.Id, Text("r1", ConversationEntryKind.Reasoning, "thinking", 2));
        service.Append(conversation.Id, Text("m2", ConversationEntryKind.Assistant, "answer", 3));

        Assert.Equal(
            new[] { "m1", "r1", "m2" },
            service.GetConversation(conversation.Id)!.Entries.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void AllStructuredEntryKindsArePreserved()
    {
        var service = CreateService();
        var conversation = service.Create();

        service.Append(conversation.Id, Text("m1", ConversationEntryKind.User, "show me products"));
        service.Append(conversation.Id, new ConversationEntry
        {
            Id = "call-1",
            Kind = ConversationEntryKind.Tool,
            Timestamp = DateTimeOffset.UtcNow,
            Tool = new AgentToolExecution("GetRecords", AgentToolExecutionStatus.Succeeded, "completed", DateTimeOffset.UtcNow)
            {
                CallId = "call-1",
                Arguments = """{"collectionName":"Product"}""",
                Response = """{"Success":true}"""
            }
        });
        service.Append(conversation.Id, new ConversationEntry
        {
            Id = "wf-1",
            Kind = ConversationEntryKind.Workflow,
            Timestamp = DateTimeOffset.UtcNow,
            Workflow = new ConversationWorkflowEntry("op-1", "WaitingForUser", "Approve?")
        });
        service.Append(conversation.Id, new ConversationEntry
        {
            Id = "rec-1",
            Kind = ConversationEntryKind.Records,
            Timestamp = DateTimeOffset.UtcNow,
            Records = new RecordsDisplayMessage(
                "Product",
                [new RecordDisplayItem("123", "Product", "Laptop", [new RecordDisplayField("Price", "1499")])],
                ["Price"], 1, [], [])
        });
        service.Append(conversation.Id, Text("m2", ConversationEntryKind.Assistant, "here they are"));

        var entries = service.GetConversation(conversation.Id)!.Entries;

        Assert.Equal(
            new[]
            {
                ConversationEntryKind.User, ConversationEntryKind.Tool, ConversationEntryKind.Workflow,
                ConversationEntryKind.Records, ConversationEntryKind.Assistant
            },
            entries.Select(entry => entry.Kind).ToArray());

        Assert.Equal("GetRecords", entries[1].Tool!.Name);
        Assert.Equal("op-1", entries[2].Workflow!.OperationId);
        Assert.Equal("Laptop", entries[3].Records!.Records[0].DisplayValue);
    }

    [Fact]
    public void ReappendingTheSameIdUpdatesInPlaceKeepingPosition()
    {
        var service = CreateService();
        var conversation = service.Create();

        service.Append(conversation.Id, new ConversationEntry
        {
            Id = "call-1", Kind = ConversationEntryKind.Tool, Timestamp = DateTimeOffset.UtcNow,
            Tool = new AgentToolExecution("GetRecords", AgentToolExecutionStatus.Started, null, DateTimeOffset.UtcNow) { CallId = "call-1" }
        });
        service.Append(conversation.Id, Text("m2", ConversationEntryKind.Assistant, "later message"));

        // The same call completes after a later message was already recorded.
        service.Append(conversation.Id, new ConversationEntry
        {
            Id = "call-1", Kind = ConversationEntryKind.Tool, Timestamp = DateTimeOffset.UtcNow,
            Tool = new AgentToolExecution("GetRecords", AgentToolExecutionStatus.Succeeded, "completed", DateTimeOffset.UtcNow) { CallId = "call-1" }
        });

        var entries = service.GetConversation(conversation.Id)!.Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal("call-1", entries[0].Id);
        Assert.Equal(AgentToolExecutionStatus.Succeeded, entries[0].Tool!.Status);
    }

    [Fact]
    public void UpdatedAtAdvancesWhenAnEntryIsAdded()
    {
        var service = CreateService();
        var created = service.Create();

        Thread.Sleep(5);
        service.Append(created.Id, Text("m1", ConversationEntryKind.User, "hello"));

        var updated = service.GetConversation(created.Id)!;
        Assert.True(updated.UpdatedAt > created.UpdatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public void FirstUserMessageBecomesTheTitle()
    {
        var service = CreateService();
        var conversation = service.Create();

        service.Append(conversation.Id, Text("m1", ConversationEntryKind.User, "Which users are in the database?"));
        service.Append(conversation.Id, Text("m2", ConversationEntryKind.User, "second question"));

        Assert.Equal("Which users are in the database?", service.GetConversation(conversation.Id)!.Title);
    }

    [Fact]
    public void ConversationsAreListedMostRecentlyUpdatedFirst()
    {
        var service = CreateService();
        var first = service.Create();
        var second = service.Create();

        Thread.Sleep(5);
        service.Append(first.Id, Text("m1", ConversationEntryKind.User, "touching the older one"));

        Assert.Equal(first.Id, service.GetConversations()[0].Id);
        Assert.Equal(second.Id, service.GetConversations()[1].Id);
    }

    [Fact]
    public void DeleteRemovesOnlyTheRequestedConversation()
    {
        var service = CreateService();
        var first = service.Create();
        var second = service.Create();

        Assert.True(service.Delete(first.Id));
        Assert.False(service.Delete(first.Id));
        Assert.Null(service.GetConversation(first.Id));
        Assert.NotNull(service.GetConversation(second.Id));
    }

    [Fact]
    public void RenameChangesTheTitle()
    {
        var service = CreateService();
        var conversation = service.Create();

        Assert.True(service.Rename(conversation.Id, "Customer analysis"));
        Assert.Equal("Customer analysis", service.GetConversation(conversation.Id)!.Title);
        Assert.False(service.Rename("missing", "x"));
    }

    [Fact]
    public void AppendingToAnUnknownConversationIsIgnored()
    {
        var service = CreateService();

        Assert.Null(service.Append("missing", Text("m1", ConversationEntryKind.User, "hello")));
        Assert.Empty(service.GetConversations());
    }

    [Fact]
    public void SnapshotsDoNotExposeMutableInternalState()
    {
        var service = CreateService();
        var conversation = service.Create();
        service.Append(conversation.Id, Text("m1", ConversationEntryKind.User, "hello"));

        var snapshot = service.GetConversation(conversation.Id)!;
        service.Append(conversation.Id, Text("m2", ConversationEntryKind.Assistant, "world"));

        // The earlier snapshot must not have grown behind the caller's back.
        Assert.Single(snapshot.Entries);
        Assert.Equal(2, service.GetConversation(conversation.Id)!.Entries.Count);
    }

    [Fact]
    public void HistoryHoldsNoUiTypes()
    {
        var service = CreateService();
        var conversation = service.Create();
        service.Append(conversation.Id, Text("m1", ConversationEntryKind.User, "hello"));

        foreach (var type in new[] { typeof(StoredConversation), typeof(ConversationEntry), typeof(ConversationWorkflowEntry) })
        {
            var assembly = type.Assembly.GetName().Name ?? string.Empty;
            Assert.DoesNotContain("Maui", assembly, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TokkDb.LLM.Application", assembly, StringComparison.OrdinalIgnoreCase);
        }
    }
}
