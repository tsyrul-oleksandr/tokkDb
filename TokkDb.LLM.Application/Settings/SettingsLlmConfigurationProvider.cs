using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Application.Settings;

/// <summary>
/// Resolves provider/model configuration per operation type from application
/// settings, falling back to the default provider configuration.
/// </summary>
public sealed class SettingsLlmConfigurationProvider : ILlmConfigurationProvider
{
    public LlmOperationConfiguration Resolve(AgentOperationType operationType)
    {
        var settings = Settings.Instance;
        var operationOverride = settings.GetOperationOverride(operationType);

        return new LlmOperationConfiguration(
            operationOverride?.Provider ?? settings.Provider,
            CoalesceRequired(operationOverride?.Url, settings.ProviderUrl),
            CoalesceRequired(operationOverride?.Model, settings.ProviderModel),
            CoalesceOptional(operationOverride?.AuthenticationToken, settings.AuthenticationToken),
            Settings.NormalizeContextSize(operationOverride?.ContextSize ?? settings.ContextSize));
    }

    private static string CoalesceRequired(string? candidate, string fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    private static string? CoalesceOptional(string? candidate, string? fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
}
