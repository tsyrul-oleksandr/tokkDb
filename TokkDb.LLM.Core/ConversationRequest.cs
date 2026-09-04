namespace TokkDb.LLM.Core;

public sealed record ConversationRequest(
    LlmProviderKind Provider,
    string Url,
    string Model,
    string Message,
    string? AuthenticationToken = null,
    string? SystemPrompt = null,
    int? ContextSize = null);
