namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Logical operation types supported by the AI orchestration layer.
/// Each type may be bound to a different provider, model and endpoint.
/// </summary>
public enum AgentOperationType
{
    Chat,
    DocumentAnalysis,
    DataProcessing,
    DataAnalysis,
    SchemaAnalysis,
    SchemaModification
}
