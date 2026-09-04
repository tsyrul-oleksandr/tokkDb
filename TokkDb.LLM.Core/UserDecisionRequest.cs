namespace TokkDb.LLM.Core;

public sealed class UserDecisionRequest
{
    /// <summary>
    /// Stable identity of this decision request. Used by the UI to avoid
    /// rendering the same workflow message twice.
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    public required string OperationId { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<WorkflowAction> AvailableActions { get; init; } = [];
}

public sealed class WorkflowAction
{
    public required string ActionId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required WorkflowDecision Decision { get; init; }
}
