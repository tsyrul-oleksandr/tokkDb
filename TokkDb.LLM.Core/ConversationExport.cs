namespace TokkDb.LLM.Core;

/// <summary>
/// Logical, UI-independent representation of a conversation for export.
///
/// These records describe what happened in the conversation - messages, tool
/// calls, workflow interactions, record displays - not how any of it was drawn.
/// The export therefore stays valid when the chat UI changes.
/// </summary>
public sealed record ConversationExport(
    int Version,
    IReadOnlyList<ConversationExportMessage> Messages);

/// <summary>
/// One conversation event. Exactly one of the structured payloads is populated,
/// according to <see cref="Role"/>.
/// </summary>
public sealed record ConversationExportMessage
{
    public required string Id { get; init; }

    /// <summary>user, assistant, system, tool, workflow, records or reasoning.</summary>
    public required string Role { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Text of a plain message. Null for a text-bearing role that produced no
    /// text; ignored entirely for structured roles.
    /// </summary>
    public string? Content { get; init; }

    public ConversationExportTool? Tool { get; init; }

    public ConversationExportWorkflow? Workflow { get; init; }

    public ConversationExportRecords? Records { get; init; }
}

/// <summary>
/// A tool invocation, taken from the structured tool-execution data rather than
/// from anything rendered. <see cref="Arguments"/>, <see cref="Result"/> and
/// <see cref="Error"/> hold the already-redacted payload text; the serializer
/// embeds them as JSON when they parse as JSON.
/// </summary>
public sealed record ConversationExportTool
{
    public required string Name { get; init; }

    /// <summary>started, completed or error.</summary>
    public required string Status { get; init; }

    public string? Arguments { get; init; }

    public string? Result { get; init; }

    public string? Error { get; init; }
}

public sealed record ConversationExportWorkflowAction(string Id, string Title);

public sealed record ConversationExportWorkflow
{
    public string? OperationId { get; init; }

    public string? Status { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<ConversationExportWorkflowAction> Actions { get; init; }
        = Array.Empty<ConversationExportWorkflowAction>();
}

/// <summary>
/// A record display. The source of truth is the collection plus the record ids
/// and requested fields - never the evaluated display value, which is derived
/// from the collection's DisplayRule at render time.
/// </summary>
public sealed record ConversationExportRecords
{
    public required string CollectionName { get; init; }

    public IReadOnlyList<string> RecordIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AdditionalFields { get; init; } = Array.Empty<string>();
}
