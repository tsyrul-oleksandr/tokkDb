namespace TokkDb.LLM.Core.Orchestration.Workflows;

/// <summary>
/// Single message type consumed by the chat agent executor. It carries either
/// the initial user message or the user's decision when a paused workflow is
/// resumed, so the workflow graph needs only one message contract.
/// </summary>
public sealed record ChatAgentInput(
    string? Message,
    WorkflowDecision? Decision = null,
    string? ActionId = null,
    string? ActionTitle = null,
    string? ActionDescription = null,
    string? AdditionalInstructions = null);

/// <summary>
/// Request emitted through the workflow's request port when the agent needs a
/// human decision.
/// </summary>
public sealed record ChatDecisionPrompt(
    string RequestId,
    string Message,
    IReadOnlyCollection<UserAction> Actions);

/// <summary>
/// Final output of one chat turn.
/// </summary>
public sealed record ChatTurnOutput(string Text, AgentTokenUsage? Usage = null);
