using Microsoft.Agents.AI.Workflows;

namespace TokkDb.LLM.Core.Orchestration;

/// <inheritdoc />
public sealed class WorkflowEventAdapter : IWorkflowEventAdapter
{
    public AgentWorkflowEvent? Adapt(
        WorkflowEvent workflowEvent,
        AgentOperationContext context,
        UserDecisionRequest? decisionRequest = null)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        ArgumentNullException.ThrowIfNull(context);

        return workflowEvent switch
        {
            RequestInfoEvent => Create(
                AgentWorkflowEventKind.WorkflowWaitingForUser,
                context,
                decisionRequest?.Message ?? "Waiting for your decision.",
                decisionRequest),

            WorkflowOutputEvent output => Create(
                AgentWorkflowEventKind.WorkflowCompleted,
                context,
                "Operation completed.",
                details: output.Data?.ToString()),

            WorkflowErrorEvent error => Create(
                AgentWorkflowEventKind.WorkflowFailed,
                context,
                "Operation failed.",
                details: error.Exception?.Message),

            ExecutorFailedEvent failure => Create(
                AgentWorkflowEventKind.WorkflowFailed,
                context,
                $"Step '{failure.ExecutorId}' failed.",
                // Message only: failure data is often an exception whose
                // ToString would carry a stack trace into the UI.
                details: failure.Data is Exception exception
                    ? exception.InnerException?.Message ?? exception.Message
                    : failure.Data?.ToString()),

            ExecutorInvokedEvent invoked => Create(
                AgentWorkflowEventKind.WorkflowProgress,
                context,
                $"Running step '{invoked.ExecutorId}'."),

            ExecutorCompletedEvent completed => Create(
                AgentWorkflowEventKind.WorkflowProgress,
                context,
                $"Step '{completed.ExecutorId}' completed."),

            _ => null
        };
    }

    public AgentWorkflowEvent Create(
        AgentWorkflowEventKind kind,
        AgentOperationContext context,
        string message,
        UserDecisionRequest? decisionRequest = null,
        string? details = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AgentWorkflowEvent(
            kind,
            context.OperationId,
            context.OperationType,
            message,
            DateTimeOffset.UtcNow,
            decisionRequest,
            details);
    }
}
