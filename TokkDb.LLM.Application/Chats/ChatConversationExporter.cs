using TokkDb.LLM.Core;

namespace TokkDb.LLM.Application.Chats;

/// <summary>
/// Maps the chat's view models onto the logical <see cref="ConversationExport"/>.
///
/// The mapping reads the structured data each message carries - tool execution
/// records, workflow models, record display models - and never the text that was
/// rendered from them. Order is the order of the message list, so tool calls stay
/// interleaved exactly where they occurred.
/// </summary>
internal static class ChatConversationExporter
{
    public static ConversationExport Build(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var exported = new List<ConversationExportMessage>();

        foreach (var message in messages)
        {
            var mapped = Map(message);
            if (mapped is not null)
            {
                exported.Add(mapped);
            }
        }

        return new ConversationExport(ConversationJsonSerializer.SchemaVersion, exported);
    }

    private static ConversationExportMessage? Map(ChatMessage message)
    {
        var id = message.Id.ToString("N");
        var timestamp = new DateTimeOffset(message.Timestamp);

        switch (message.Kind)
        {
            case ChatMessageKind.ToolExecution when message.ToolExecution is not null:
            {
                var tool = message.ToolExecution;
                return new ConversationExportMessage
                {
                    Id = string.IsNullOrEmpty(tool.CallId) ? id : tool.CallId,
                    Role = "tool",
                    Timestamp = tool.TimestampUtc,
                    Tool = new ConversationExportTool
                    {
                        Name = tool.Name,
                        Status = tool.StatusText.ToLowerInvariant(),
                        Arguments = tool.Arguments,
                        Result = tool.Response,
                        Error = tool.Error
                    }
                };
            }

            case ChatMessageKind.Workflow when message.Workflow is not null:
            {
                var workflow = message.Workflow;
                return new ConversationExportMessage
                {
                    Id = id,
                    Role = "workflow",
                    Timestamp = timestamp,
                    Workflow = new ConversationExportWorkflow
                    {
                        OperationId = workflow.WorkflowOperationId,
                        Status = workflow.WorkflowStatus,
                        Message = string.IsNullOrWhiteSpace(workflow.Message)
                            ? message.Content
                            : workflow.Message,
                        Actions = workflow.AvailableActions
                            .Select(action => new ConversationExportWorkflowAction(action.ActionId, action.Title))
                            .ToArray()
                    }
                };
            }

            case ChatMessageKind.Records when message.Records is not null:
            {
                var records = message.Records;
                return new ConversationExportMessage
                {
                    Id = id,
                    Role = "records",
                    Timestamp = timestamp,
                    Records = new ConversationExportRecords
                    {
                        CollectionName = records.CollectionName,
                        RecordIds = records.Records.Select(record => record.RecordId).ToArray(),
                        AdditionalFields = records.RequestedAdditionalFields
                    }
                };
            }

            case ChatMessageKind.Reasoning when message.Reasoning is not null:
                return new ConversationExportMessage
                {
                    Id = id,
                    Role = "reasoning",
                    Timestamp = timestamp,
                    Content = NullIfEmpty(message.Reasoning.Content)
                };

            case ChatMessageKind.Text:
                return new ConversationExportMessage
                {
                    Id = id,
                    Role = MapRole(message.Role),
                    Timestamp = timestamp,
                    Content = NullIfEmpty(message.Content)
                };

            default:
                // A structured message missing its payload carries nothing to export.
                return null;
        }
    }

    private static string MapRole(ChatMessageRole role) => role switch
    {
        ChatMessageRole.User => "user",
        ChatMessageRole.Assistant => "assistant",
        ChatMessageRole.System => "system",
        ChatMessageRole.Workflow => "workflow",
        _ => "assistant"
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
