using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage.Engine;

namespace TokkDb.Assistant.TokenBudget;

/// <summary>
/// The application composed for the console: a database file, the engine-backed storage and its
/// recorder, and the model - the real one over the native transport under the egress mode, or
/// the scripted fake. The same wiring the application's composition root does (AG-1f, NF-4).
/// </summary>
public sealed class Composition : IDisposable
{
    private Composition(TokkDbStorage storage, ServiceProvider services, Orchestrator orchestrator, ScriptedModel? scripted, Harness.ProbingChatClient? probe)
    {
        Storage = storage;
        Services = services;
        Orchestrator = orchestrator;
        Scripted = scripted;
        Probe = probe;
    }

    public TokkDbStorage Storage { get; }
    public ServiceProvider Services { get; }
    public Orchestrator Orchestrator { get; }
    public ScriptedModel? Scripted { get; }

    /// <summary>The single-token probe in front of the real model, when the composition was asked for one (§6.2a).</summary>
    public Harness.ProbingChatClient? Probe { get; }

    public OperationRunner Runner => Services.GetRequiredService<OperationRunner>();

    public static Composition OverOllama(string databasePath, ModelEndpoint? endpoint = null, EgressMode mode = EgressMode.LocalOnly, ModelSettings? settings = null, OrchestratorOptions? options = null, bool probe = false)
    {
        var storage = new TokkDbStorage(databasePath);
        var services = new ServiceCollection()
            .AddOllama(endpoint ?? ModelEndpoint.Ollama, mode)
            .AddAssistantAgents(settings)
            .AddSingleton(storage.Traces)
            .AddSingleton(new ToolCatalog(storage));

        Harness.ProbingChatClient? probing = null;
        if (probe)
        {
            // In front of the transport the same way the runner sees it: the last registration
            // of the client is the one resolved (AG-1f), and it wraps the one AddOllama made.
            var address = (endpoint ?? ModelEndpoint.Ollama).Address;
            services.AddSingleton<IChatClient>(_ =>
            {
                var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromMinutes(5) };
                return probing = new Harness.ProbingChatClient(new OllamaSharp.OllamaApiClient(http, ModelConfiguration.Default.Model));
            });
        }

        var provider = services.BuildServiceProvider();
        var orchestrator = new Orchestrator(storage, storage.Traces, provider.GetRequiredService<OperationRunner>(), provider.GetRequiredService<ToolCatalog>(), options);
        if (probe) _ = provider.GetRequiredService<IChatClient>();

        return new Composition(storage, provider, orchestrator, null, probing);
    }

    public static Composition OverScript(string databasePath, ScriptedModel? scripted = null, OrchestratorOptions? options = null)
    {
        var storage = new TokkDbStorage(databasePath);
        var model = scripted ?? new ScriptedModel();
        var services = new ServiceCollection()
            .AddScriptedModel(model)
            .AddAssistantAgents()
            .AddSingleton(storage.Traces)
            .AddSingleton(new ToolCatalog(storage))
            .BuildServiceProvider();

        return new Composition(storage, services, new Orchestrator(storage, storage.Traces, services.GetRequiredService<OperationRunner>(), services.GetRequiredService<ToolCatalog>(), options), model, null);
    }

    public void Dispose()
    {
        Services.Dispose();
        Storage.Dispose();
    }
}
