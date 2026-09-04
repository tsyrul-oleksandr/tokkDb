namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Application-level context that survives agent handoff, workflow execution,
/// pause, resume and user decisions. Contains no authentication material.
/// </summary>
public sealed record AgentOperationContext(
    string OperationId,
    AgentOperationType OperationType,
    string ConversationId,
    LlmProviderKind Provider,
    string Model,
    string EndpointReference,
    DateTimeOffset StartedUtc)
{
    public static AgentOperationContext Create(
        AgentOperationType operationType,
        string conversationId,
        LlmOperationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new AgentOperationContext(
            Guid.NewGuid().ToString("N"),
            operationType,
            string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString("N") : conversationId,
            configuration.Provider,
            configuration.Model,
            configuration.EndpointReference,
            DateTimeOffset.UtcNow);
    }
}
