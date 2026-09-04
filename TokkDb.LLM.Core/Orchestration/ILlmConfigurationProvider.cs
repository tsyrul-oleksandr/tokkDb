namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Resolves the provider/model configuration for a given operation type.
/// Implementations fall back to the application default when an operation
/// has no explicit override.
/// </summary>
public interface ILlmConfigurationProvider
{
    LlmOperationConfiguration Resolve(AgentOperationType operationType);
}
