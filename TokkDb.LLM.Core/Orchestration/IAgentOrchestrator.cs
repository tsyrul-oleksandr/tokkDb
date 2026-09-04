namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Application-level entry point for AI-driven operations.
/// The MAUI layer depends only on this abstraction; the Microsoft Agent
/// Framework implementation lives behind it.
/// </summary>
public interface IAgentOrchestrator
{
    event EventHandler<AgentWorkflowEventArgs>? WorkflowEventRaised;

    event EventHandler<AgentToolExecutionEventArgs>? ToolExecutionStatusChanged;

    /// <summary>
    /// Raised while a model streams reasoning output. Carries only the
    /// provider-independent reasoning representation.
    /// </summary>
    event EventHandler<AgentReasoningEventArgs>? ReasoningUpdated;

    /// <summary>
    /// Raised when records should be rendered in the chat as a structured list.
    /// </summary>
    event EventHandler<RecordsDisplayEventArgs>? RecordsDisplayRequested;

    /// <summary>
    /// Returns the operation currently waiting for user input, if any.
    /// </summary>
    AgentOperationResult? GetActiveOperation();

    Task<AgentOperationResult> ExecuteAsync(
        AgentOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentOperationResult> ResumeAsync(
        AgentResumeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops all conversational and workflow state (new chat).
    /// </summary>
    void Reset();
}
