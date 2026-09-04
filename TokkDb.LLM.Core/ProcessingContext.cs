namespace TokkDb.LLM.Core;

public sealed record ProcessingContext(
    Guid WorkflowId,
    ProcessingState State,
    ConversationRequest ProviderConfiguration,
    string InitialMessage,
    string? LastAgentResponse,
    UserInteractionRequest? PendingUserInteraction,
    UserDecisionRequest? PendingDecisionRequest,
    UserAction? SelectedAction,
    string? FailureReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyCollection<string> Timeline,
    IReadOnlyDictionary<string, string?>? OperationContext = null,
    string? AdditionalInstructions = null);
