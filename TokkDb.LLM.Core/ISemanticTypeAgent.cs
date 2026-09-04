namespace TokkDb.LLM.Core;

public interface ISemanticTypeAgent
{
    Task<SemanticTypeResolutionResult> ResolveAsync(
        ConversationRequest providerConfiguration,
        SemanticTypeResolutionInput input,
        CancellationToken cancellationToken = default);
}
