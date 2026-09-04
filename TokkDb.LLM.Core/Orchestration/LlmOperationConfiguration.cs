namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Provider/model configuration bound to a single <see cref="AgentOperationType"/>.
/// </summary>
public sealed record LlmOperationConfiguration(
    LlmProviderKind Provider,
    string Url,
    string Model,
    string? AuthenticationToken = null,
    int? ContextSize = null)
{
    /// <summary>
    /// Context window the model is asked to use, in tokens. Null leaves the
    /// provider's own default in place.
    /// </summary>
    public int? ContextSize { get; init; } = ContextSize;

    /// <summary>
    /// Safe endpoint reference for diagnostics and operation context.
    /// Never contains authentication material.
    /// </summary>
    public string EndpointReference => $"{Provider}:{Url}";

    public ConversationRequest ToConversationRequest(string message, string? systemPrompt = null)
    {
        return new ConversationRequest(
            Provider, Url, Model, message, AuthenticationToken, systemPrompt, ContextSize);
    }
}
