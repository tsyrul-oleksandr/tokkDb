namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Application-level workflow event kinds. These are the only workflow events
/// the UI is allowed to observe.
/// </summary>
public enum AgentWorkflowEventKind
{
    WorkflowStarted,
    WorkflowProgress,
    WorkflowWaitingForUser,
    WorkflowResumed,
    WorkflowCompleted,
    WorkflowCancelled,
    WorkflowFailed
}

public sealed record AgentWorkflowEvent(
    AgentWorkflowEventKind Kind,
    string OperationId,
    AgentOperationType OperationType,
    string Message,
    DateTimeOffset TimestampUtc,
    UserDecisionRequest? DecisionRequest = null,
    string? Details = null);

public sealed class AgentWorkflowEventArgs : EventArgs
{
    public AgentWorkflowEventArgs(AgentWorkflowEvent workflowEvent)
    {
        WorkflowEvent = workflowEvent;
    }

    public AgentWorkflowEvent WorkflowEvent { get; }
}
