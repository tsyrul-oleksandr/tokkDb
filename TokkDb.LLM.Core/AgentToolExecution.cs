namespace TokkDb.LLM.Core;

public enum AgentToolExecutionStatus
{
    Started,
    Succeeded,
    Failed
}

/// <summary>
/// Application-level record of a single tool call transition.
///
/// This is the provider-independent model the UI renders: it carries no
/// Microsoft Agent Framework, Microsoft.Extensions.AI or provider types, and the
/// payload strings are already formatted and redacted by
/// <see cref="ToolPayloadFormatter"/> before they reach it.
///
/// <see cref="CallId"/> correlates the <see cref="AgentToolExecutionStatus.Started"/>
/// transition with the later success or failure of the same call, so the UI can
/// update one message in place instead of appending a second one.
/// </summary>
public sealed record AgentToolExecution(
    string Name,
    AgentToolExecutionStatus Status,
    string? Details,
    DateTimeOffset TimestampUtc)
{
    /// <summary>
    /// Identifier shared by every transition of one tool call.
    /// </summary>
    public string CallId { get; init; } = string.Empty;

    /// <summary>
    /// Formatted request body (the arguments passed to the tool), or
    /// <c>null</c> when the call takes no arguments.
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Formatted response body. Populated on successful completion.
    /// </summary>
    public string? Response { get; init; }

    /// <summary>
    /// Error message and any relevant details. Populated on failure.
    /// </summary>
    public string? Error { get; init; }
}

public sealed class AgentToolExecutionEventArgs : EventArgs
{
    public AgentToolExecutionEventArgs(AgentToolExecution execution)
    {
        Execution = execution;
    }

    public AgentToolExecution Execution { get; }
}
