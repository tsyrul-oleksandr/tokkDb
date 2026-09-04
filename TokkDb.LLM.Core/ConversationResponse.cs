namespace TokkDb.LLM.Core;

public sealed record ConversationResponse(
    string Text,
    IReadOnlyCollection<AgentToolExecution> ToolExecutions,
    UserInteractionRequest? UserInteractionRequest = null)
{
    /// <summary>
    /// Reasoning produced during this turn, in the order the model emitted it.
    /// Empty when the provider or model does not return reasoning.
    /// </summary>
    public IReadOnlyCollection<AgentReasoningSegment> Reasoning { get; init; }
        = Array.Empty<AgentReasoningSegment>();

    /// <summary>
    /// Tokens the turn consumed. Null when the provider reports no usage.
    /// </summary>
    public AgentTokenUsage? Usage { get; init; }
}
