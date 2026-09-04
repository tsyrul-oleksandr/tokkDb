using Microsoft.Extensions.DependencyInjection;
using TokkDb.LLM.Core.Diagnostics;
using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IAppInfoProvider, AppInfoProvider>();
        services.AddHttpClient<ILLMProvider, HttpLlmProvider>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        // In-memory only: conversations live for the lifetime of the process.
        // Swapping in a persistent implementation needs no change to the chat UI.
        services.AddSingleton<IConversationHistoryService, InMemoryConversationHistoryService>();
        services.AddSingleton<IConversationAgent, ConversationAgent>();
        services.AddSingleton<ISemanticTypeAgent, SemanticTypeAgent>();
        services.AddSingleton<IProcessingWorkflowService, ProcessingWorkflowService>();
        services.AddSingleton<IDocumentProcessingWorkflowService, DocumentProcessingWorkflowService>();
        return services;
    }

    /// <summary>
    /// Registers the AI orchestration layer. The Microsoft Agent Framework
    /// implementation is only reachable through <see cref="IAgentOrchestrator"/>.
    /// </summary>
    /// <remarks>
    /// The host application must register an <see cref="ILlmConfigurationProvider"/>
    /// before resolving the orchestrator.
    /// </remarks>
    public static IServiceCollection AddAgentOrchestration(this IServiceCollection services)
    {
        services.AddSingleton<IWorkflowEventAdapter, WorkflowEventAdapter>();
        services.AddSingleton<IAgentOrchestrator, MicrosoftAgentOrchestrator>();
        return services;
    }
}
