namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Request to start an AI operation. The UI layer builds this and never
/// references Microsoft Agent Framework types.
/// </summary>
public sealed record AgentOperationRequest(
    AgentOperationType OperationType,
    string ConversationId,
    string Message,
    string? DocumentPath = null,
    string? SystemPrompt = null);

/// <summary>
/// Request to resume an operation that is waiting for a user decision.
/// </summary>
public sealed record AgentResumeRequest(
    string OperationId,
    WorkflowDecision Decision,
    string? ActionId = null,
    string? AdditionalInstructions = null);

/// <summary>
/// Outcome of a single orchestration turn.
/// </summary>
public sealed record AgentOperationResult(
    AgentOperationContext Context,
    ProcessingState State,
    string? Text,
    UserDecisionRequest? PendingDecision,
    IReadOnlyCollection<AgentToolExecution> ToolExecutions,
    IReadOnlyCollection<string> Timeline,
    string? StatusMessage = null,
    string? FailureReason = null,
    AgentTokenUsage? Usage = null)
{
    public bool IsWaitingForUser => State == ProcessingState.WaitingForUser && PendingDecision is not null;

    public bool IsTerminal => State is ProcessingState.Completed or ProcessingState.Cancelled or ProcessingState.Failed;
}
