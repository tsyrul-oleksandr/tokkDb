namespace TokkDb.LLM.Core;

public enum ConversationEntryKind
{
    User,
    Assistant,
    System,
    Reasoning,
    Tool,
    Workflow,
    Records
}

/// <summary>
/// Workflow interaction as stored in history: the structured facts only, never
/// the view model that rendered them.
/// </summary>
public sealed record ConversationWorkflowEntry(
    string OperationId,
    string Status,
    string Message,
    UserDecisionRequest? DecisionRequest = null);

/// <summary>
/// One event in a conversation.
///
/// Everything here is a plain Core type - no commands, colours, observable
/// objects or view models - so history stays independent of how the chat is
/// drawn and can later be persisted unchanged.
///
/// <see cref="Id"/> is the natural key of the event: the message id for text,
/// the call id for a tool, the segment id for reasoning. Appending an entry
/// whose id already exists updates that entry in place, which is how a tool
/// call that moves from Started to Completed stays a single history event and
/// keeps its original position.
/// </summary>
public sealed record ConversationEntry
{
    public required string Id { get; init; }

    public required ConversationEntryKind Kind { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Text of a user, assistant, system or reasoning entry.</summary>
    public string? Text { get; init; }

    public AgentToolExecution? Tool { get; init; }

    public ConversationWorkflowEntry? Workflow { get; init; }

    public RecordsDisplayMessage? Records { get; init; }
}

/// <summary>
/// A conversation and its ordered events. Immutable snapshot: the service hands
/// these out so callers cannot mutate stored state by accident.
/// </summary>
public sealed record StoredConversation(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationEntry> Entries)
{
    public const string UntitledConversation = "New Chat";

    public int MessageCount => Entries.Count;
}
