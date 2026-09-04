using TokkDb.LLM.Core;

namespace TokkDb.LLM.Application.Settings;

/// <summary>
/// Optional per-operation provider override. Any field left empty falls back to
/// the application default provider configuration.
/// </summary>
public sealed class OperationProviderSettings
{
    public LlmProviderKind? Provider { get; set; }

    public string? Url { get; set; }

    public string? Model { get; set; }

    public string? AuthenticationToken { get; set; }

    /// <summary>Context window override in tokens. Null uses the default.</summary>
    public int? ContextSize { get; set; }

    public bool IsEmpty =>
        Provider is null &&
        string.IsNullOrWhiteSpace(Url) &&
        string.IsNullOrWhiteSpace(Model) &&
        string.IsNullOrWhiteSpace(AuthenticationToken) &&
        ContextSize is null;
}
