namespace TokkDb.LLM.Core;

public interface IProcessingWorkflowService
{
    ProcessingContext? GetCurrentContext();

    Task<ProcessingContext> StartAsync(ConversationRequest request, CancellationToken cancellationToken = default);

    Task<ProcessingContext> ResumeAsync(
        string operationId,
        WorkflowDecision decision,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default);
}
