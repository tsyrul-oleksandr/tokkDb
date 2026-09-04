namespace TokkDb.LLM.Core;

public sealed record LlmRequest(
    LlmProviderKind Provider,
    string Url,
    string Model,
    string Prompt,
    string? AuthenticationToken = null,
    string? SystemPrompt = null);
