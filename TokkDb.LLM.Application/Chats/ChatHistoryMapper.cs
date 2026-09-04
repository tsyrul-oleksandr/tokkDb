using System.Windows.Input;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Application.Chats;

/// <summary>
/// Translates between the chat's view models and the UI-independent history
/// entries.
///
/// Only structured data crosses the boundary: commands, colours and observable
/// wrappers are rebuilt when a conversation is restored, never stored.
/// </summary>
internal static class ChatHistoryMapper
{
    /// <summary>
    /// Builds the history entry for a rendered message, or null when the message
    /// carries nothing worth storing.
    ///
    /// The entry id is the event's natural key - call id for a tool, segment id
    /// for reasoning - so later transitions of the same event update it in place.
    /// </summary>
    public static ConversationEntry? ToEntry(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var id = message.Id.ToString("N");
        var timestamp = new DateTimeOffset(message.Timestamp);

        switch (message.Kind)
        {
            case ChatMessageKind.ToolExecution when message.ToolExecution is not null:
            {
                var tool = message.ToolExecution;
                return new ConversationEntry
                {
                    Id = string.IsNullOrEmpty(tool.CallId) ? id : tool.CallId,
                    Kind = ConversationEntryKind.Tool,
                    Timestamp = tool.TimestampUtc,
                    Tool = new AgentToolExecution(
                        tool.Name,
                        ParseStatus(tool.Status),
                        null,
                        tool.TimestampUtc)
                    {
                        CallId = tool.CallId,
                        Arguments = tool.Arguments,
                        Response = tool.Response,
                        Error = tool.Error
                    }
                };
            }

            case ChatMessageKind.Workflow when message.Workflow is not null:
            {
                var workflow = message.Workflow;
                return new ConversationEntry
                {
                    Id = id,
                    Kind = ConversationEntryKind.Workflow,
                    Timestamp = timestamp,
                    Workflow = new ConversationWorkflowEntry(
                        workflow.WorkflowOperationId,
                        workflow.WorkflowStatus,
                        string.IsNullOrWhiteSpace(workflow.Message) ? message.Content : workflow.Message,
                        workflow.DecisionRequest)
                };
            }

            case ChatMessageKind.Records when message.Records is not null:
            {
                var records = message.Records;
                return new ConversationEntry
                {
                    Id = id,
                    Kind = ConversationEntryKind.Records,
                    Timestamp = timestamp,
                    Records = new RecordsDisplayMessage(
                        records.CollectionName,
                        records.Records
                            .Select(record => new RecordDisplayItem(
                                record.RecordId,
                                record.CollectionName,
                                record.DisplayValue,
                                record.AdditionalFields
                                    .Select(field => new RecordDisplayField(field.Name, field.Value))
                                    .ToArray()))
                            .ToArray(),
                        records.RequestedAdditionalFields,
                        records.Records.Count,
                        Array.Empty<string>(),
                        Array.Empty<string>())
                };
            }

            case ChatMessageKind.Reasoning when message.Reasoning is not null:
                return new ConversationEntry
                {
                    Id = string.IsNullOrEmpty(message.Reasoning.SegmentId)
                        ? id
                        : message.Reasoning.SegmentId,
                    Kind = ConversationEntryKind.Reasoning,
                    Timestamp = timestamp,
                    Text = message.Reasoning.Content
                };

            case ChatMessageKind.Text:
                return new ConversationEntry
                {
                    Id = id,
                    Kind = message.Role switch
                    {
                        ChatMessageRole.User => ConversationEntryKind.User,
                        ChatMessageRole.System => ConversationEntryKind.System,
                        _ => ConversationEntryKind.Assistant
                    },
                    Timestamp = timestamp,
                    Text = message.Content
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Rebuilds a rendered message from a stored entry. Interactive parts are
    /// recreated here, which is why <paramref name="openRecord"/> is supplied by
    /// the view model rather than stored.
    /// </summary>
    public static ChatMessage? ToMessage(ConversationEntry entry, Action<string, string> openRecord)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(openRecord);

        var timestamp = entry.Timestamp.LocalDateTime;

        switch (entry.Kind)
        {
            case ConversationEntryKind.Tool when entry.Tool is not null:
            {
                var model = new ToolExecutionModel
                {
                    CallId = entry.Tool.CallId,
                    Name = entry.Tool.Name,
                    TimestampUtc = entry.Tool.TimestampUtc
                };
                model.Apply(entry.Tool);

                return new ChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Kind = ChatMessageKind.ToolExecution,
                    Content = entry.Tool.Name,
                    Timestamp = timestamp,
                    ToolExecution = model
                };
            }

            case ConversationEntryKind.Workflow when entry.Workflow is not null:
            {
                var workflow = entry.Workflow;
                var actions = workflow.DecisionRequest?.AvailableActions
                    .Select(action => new WorkflowActionModel
                    {
                        WorkflowOperationId = workflow.OperationId,
                        Action = action
                    })
                    .ToArray() ?? Array.Empty<WorkflowActionModel>();

                return new ChatMessage
                {
                    Role = ChatMessageRole.Workflow,
                    Kind = ChatMessageKind.Workflow,
                    Content = workflow.Message,
                    Timestamp = timestamp,
                    Workflow = new WorkflowModel
                    {
                        WorkflowOperationId = workflow.OperationId,
                        WorkflowStatus = workflow.Status,
                        DecisionRequest = workflow.DecisionRequest,
                        Message = workflow.Message,
                        AvailableActions = actions
                    }
                };
            }

            case ConversationEntryKind.Records when entry.Records is not null:
            {
                var records = entry.Records;
                return new ChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Kind = ChatMessageKind.Records,
                    Content = string.Empty,
                    Timestamp = timestamp,
                    Records = new RecordsDisplayModel
                    {
                        CollectionName = records.CollectionName,
                        RequestedAdditionalFields = records.RequestedAdditionalFields,
                        Records = records.Records
                            .Select(record => new RecordDisplayItemModel
                            {
                                RecordId = record.RecordId,
                                CollectionName = record.CollectionName,
                                DisplayValue = record.DisplayValue,
                                AdditionalFields = record.AdditionalFields
                                    .Select(field => new RecordFieldModel { Name = field.Name, Value = field.Value })
                                    .ToArray(),
                                OpenCommand = BuildOpenCommand(openRecord, record.CollectionName, record.RecordId)
                            })
                            .ToArray()
                    }
                };
            }

            case ConversationEntryKind.Reasoning:
            {
                var reasoning = new ReasoningModel { SegmentId = entry.Id, IsStreaming = false };
                reasoning.Append(entry.Text ?? string.Empty);

                return new ChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Kind = ChatMessageKind.Reasoning,
                    Content = string.Empty,
                    Timestamp = timestamp,
                    Reasoning = reasoning
                };
            }

            case ConversationEntryKind.User:
            case ConversationEntryKind.Assistant:
            case ConversationEntryKind.System:
                return new ChatMessage
                {
                    Role = entry.Kind switch
                    {
                        ConversationEntryKind.User => ChatMessageRole.User,
                        ConversationEntryKind.System => ChatMessageRole.System,
                        _ => ChatMessageRole.Assistant
                    },
                    Kind = ChatMessageKind.Text,
                    Content = entry.Text ?? string.Empty,
                    Timestamp = timestamp
                };

            default:
                return null;
        }
    }

    private static ICommand BuildOpenCommand(
        Action<string, string> openRecord,
        string collectionName,
        string recordId) =>
        new Command(() => openRecord(collectionName, recordId));

    private static AgentToolExecutionStatus ParseStatus(string status) =>
        Enum.TryParse<AgentToolExecutionStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : AgentToolExecutionStatus.Started;
}
