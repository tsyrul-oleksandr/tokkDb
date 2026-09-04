namespace TokkDb.LLM.Core;

public interface IDocumentProcessingWorkflowService
{
    DocumentProcessingContext? GetCurrentContext();

    Task<DocumentProcessingContext> StartAsync(
        string filePath,
        ConversationRequest providerConfiguration,
        CancellationToken cancellationToken = default);

    Task<DocumentProcessingContext> ResumeAsync(
        string operationId,
        WorkflowDecision decision,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default);
}
