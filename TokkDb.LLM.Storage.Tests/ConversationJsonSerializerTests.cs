using System.Text.Json;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// The exported JSON must describe the conversation, preserve event order and
/// carry structured tool/workflow/records data rather than rendered text.
/// </summary>
public sealed class ConversationJsonSerializerTests
{
    private static DateTimeOffset At(int second) =>
        new(2026, 9, 2, 16, 20, second, TimeSpan.Zero);

    private static JsonElement Parse(ConversationExport export) =>
        JsonDocument.Parse(ConversationJsonSerializer.Serialize(export)).RootElement;

    private static ConversationExport SampleConversation() => new(
        ConversationJsonSerializer.SchemaVersion,
        [
            new ConversationExportMessage
            {
                Id = "msg-1", Role = "user", Timestamp = At(0),
                Content = "Display the latest products."
            },
            new ConversationExportMessage
            {
                Id = "tool-1", Role = "tool", Timestamp = At(1),
                Tool = new ConversationExportTool
                {
                    Name = "Query",
                    Status = "completed",
                    Arguments = """{"collectionName":"Product"}""",
                    Result = """{"recordIds":["123","456"]}"""
                }
            },
            new ConversationExportMessage
            {
                Id = "tool-2", Role = "tool", Timestamp = At(2),
                Tool = new ConversationExportTool
                {
                    Name = "ShowRecords",
                    Status = "completed",
                    Arguments = """{"collectionName":"Product","recordIds":["123","456"]}"""
                }
            },
            new ConversationExportMessage
            {
                Id = "msg-2", Role = "records", Timestamp = At(2),
                Records = new ConversationExportRecords
                {
                    CollectionName = "Product",
                    RecordIds = ["123", "456"],
                    AdditionalFields = ["Price"]
                }
            },
            new ConversationExportMessage
            {
                Id = "msg-3", Role = "assistant", Timestamp = At(3), Content = null
            }
        ]);

    [Fact]
    public void ExportHasVersionAndMessages()
    {
        var root = Parse(SampleConversation());

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(5, root.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void ChronologicalOrderIsPreservedAndNotGroupedByType()
    {
        var messages = Parse(SampleConversation()).GetProperty("messages");

        var roles = messages.EnumerateArray()
            .Select(message => message.GetProperty("role").GetString())
            .ToArray();

        // Tool calls stay interleaved where they happened, not collected at the end.
        Assert.Equal(new[] { "user", "tool", "tool", "records", "assistant" }, roles);
    }

    [Fact]
    public void ToolArgumentsAndResultAreEmbeddedAsJsonNotEscapedStrings()
    {
        var tool = Parse(SampleConversation()).GetProperty("messages")[1].GetProperty("tool");

        Assert.Equal("Query", tool.GetProperty("name").GetString());
        Assert.Equal("completed", tool.GetProperty("status").GetString());

        var arguments = tool.GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal("Product", arguments.GetProperty("collectionName").GetString());

        var result = tool.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(2, result.GetProperty("recordIds").GetArrayLength());
    }

    [Fact]
    public void AbsentToolPropertiesAreOmitted()
    {
        var tool = Parse(SampleConversation()).GetProperty("messages")[2].GetProperty("tool");

        Assert.True(tool.TryGetProperty("arguments", out _));
        Assert.False(tool.TryGetProperty("result", out _));
        Assert.False(tool.TryGetProperty("error", out _));
    }

    [Fact]
    public void FailedToolExportsErrorInsteadOfResult()
    {
        var export = new ConversationExport(1, [
            new ConversationExportMessage
            {
                Id = "tool-1", Role = "tool", Timestamp = At(1),
                Tool = new ConversationExportTool
                {
                    Name = "Query",
                    Status = "error",
                    Arguments = """{"collectionName":"Product"}""",
                    Error = "Collection not found."
                }
            }
        ]);

        var tool = Parse(export).GetProperty("messages")[0].GetProperty("tool");

        Assert.Equal("error", tool.GetProperty("status").GetString());
        Assert.Equal("Collection not found.", tool.GetProperty("error").GetString());
        Assert.False(tool.TryGetProperty("result", out _));
    }

    [Fact]
    public void NonJsonToolPayloadIsExportedAsText()
    {
        var export = new ConversationExport(1, [
            new ConversationExportMessage
            {
                Id = "tool-1", Role = "tool", Timestamp = At(1),
                Tool = new ConversationExportTool
                {
                    Name = "Query", Status = "completed", Result = "not json at all"
                }
            }
        ]);

        var result = Parse(export).GetProperty("messages")[0].GetProperty("tool").GetProperty("result");

        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal("not json at all", result.GetString());
    }

    [Fact]
    public void TruncatedJsonPayloadDoesNotBreakTheExport()
    {
        var export = new ConversationExport(1, [
            new ConversationExportMessage
            {
                Id = "tool-1", Role = "tool", Timestamp = At(1),
                Tool = new ConversationExportTool
                {
                    Name = "Query", Status = "completed", Result = """{"recordIds":["12"""
                }
            }
        ]);

        var result = Parse(export).GetProperty("messages")[0].GetProperty("tool").GetProperty("result");

        Assert.Equal(JsonValueKind.String, result.ValueKind);
    }

    [Fact]
    public void RecordsExportStructuredIdsNotRenderedDisplayValues()
    {
        var records = Parse(SampleConversation()).GetProperty("messages")[3].GetProperty("records");

        Assert.Equal("Product", records.GetProperty("collectionName").GetString());
        Assert.Equal(new[] { "123", "456" },
            records.GetProperty("recordIds").EnumerateArray().Select(id => id.GetString()).ToArray());
        Assert.Equal(new[] { "Price" },
            records.GetProperty("additionalFields").EnumerateArray().Select(f => f.GetString()).ToArray());

        // The evaluated DisplayValue is derived from the DisplayRule, so it is
        // deliberately not part of the export.
        Assert.False(records.TryGetProperty("displayValue", out _));
        Assert.False(records.TryGetProperty("records", out _));
    }

    [Fact]
    public void WorkflowIsExportedStructurallyWithActions()
    {
        var export = new ConversationExport(1, [
            new ConversationExportMessage
            {
                Id = "wf-1", Role = "workflow", Timestamp = At(1),
                Workflow = new ConversationExportWorkflow
                {
                    OperationId = "operation-123",
                    Status = "waitingForUser",
                    Message = "The schema change requires approval.",
                    Actions =
                    [
                        new ConversationExportWorkflowAction("approve", "Approve"),
                        new ConversationExportWorkflowAction("reject", "Reject")
                    ]
                }
            }
        ]);

        var workflow = Parse(export).GetProperty("messages")[0].GetProperty("workflow");

        Assert.Equal("operation-123", workflow.GetProperty("operationId").GetString());
        Assert.Equal("waitingForUser", workflow.GetProperty("status").GetString());
        Assert.Equal("The schema change requires approval.", workflow.GetProperty("message").GetString());
        Assert.Equal(2, workflow.GetProperty("actions").GetArrayLength());
        Assert.Equal("approve", workflow.GetProperty("actions")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void AssistantMessageWithNoTextExportsNullContent()
    {
        var message = Parse(SampleConversation()).GetProperty("messages")[4];

        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);
    }

    [Fact]
    public void StructuredMessagesDoNotCarryContent()
    {
        var messages = Parse(SampleConversation()).GetProperty("messages");

        Assert.False(messages[1].TryGetProperty("content", out _));
        Assert.False(messages[3].TryGetProperty("content", out _));
    }

    [Fact]
    public void TimestampsAreIso8601Utc()
    {
        var timestamp = Parse(SampleConversation()).GetProperty("messages")[0]
            .GetProperty("timestamp").GetString();

        Assert.Equal("2026-09-02T16:20:00Z", timestamp);
    }

    [Fact]
    public void LocalTimestampsAreConvertedToUtc()
    {
        var export = new ConversationExport(1, [
            new ConversationExportMessage
            {
                Id = "msg-1", Role = "user",
                Timestamp = new DateTimeOffset(2026, 9, 2, 18, 20, 0, TimeSpan.FromHours(2)),
                Content = "hi"
            }
        ]);

        Assert.Equal(
            "2026-09-02T16:20:00Z",
            Parse(export).GetProperty("messages")[0].GetProperty("timestamp").GetString());
    }

    [Fact]
    public void EmptyConversationSerializesToValidJson()
    {
        var root = Parse(new ConversationExport(1, Array.Empty<ConversationExportMessage>()));

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(0, root.GetProperty("messages").GetArrayLength());
    }
}
