using Microsoft.Agents.AI.Workflows;

namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Boundary between Microsoft Agent Framework workflow events and
/// application-level workflow events. This is the only component that is
/// allowed to translate framework event objects.
/// </summary>
public interface IWorkflowEventAdapter
{
    /// <summary>
    /// Translates a framework workflow event. Returns <c>null</c> when the
    /// event carries no application-level meaning.
    /// </summary>
    AgentWorkflowEvent? Adapt(
        WorkflowEvent workflowEvent,
        AgentOperationContext context,
        UserDecisionRequest? decisionRequest = null);

    /// <summary>
    /// Builds an application-level event that has no framework counterpart
    /// (workflow started, resumed, cancelled by the user).
    /// </summary>
    AgentWorkflowEvent Create(
        AgentWorkflowEventKind kind,
        AgentOperationContext context,
        string message,
        UserDecisionRequest? decisionRequest = null,
        string? details = null);
}
