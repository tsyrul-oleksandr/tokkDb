using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// A composition root for the orchestration tests (D-12): the in-memory storage, the in-memory
/// recorder, the scripted model, and the runner wired the way the application wires them - so a
/// test drives the same runner the application does, with no Ollama running.
/// </summary>
internal sealed class AgentsHost : IDisposable
{
    public AgentsHost(IStorage? storage = null, ITraceRecorder? recorder = null, ModelSettings? settings = null, ToolCatalog? tools = null, OrchestratorOptions? options = null)
    {
        Storage = storage ?? new MemoryStorage();
        Recorder = recorder ?? new MemoryTraceRecorder();
        Model = new ScriptedModel();
        Tools = tools ?? new ToolCatalog(Storage);

        Services = new ServiceCollection()
            .AddScriptedModel(Model)
            .AddAssistantAgents(settings)
            .AddSingleton(Recorder)
            .AddSingleton(Tools)
            .AddSingleton(Storage)
            .BuildServiceProvider();

        Runner = Services.GetRequiredService<OperationRunner>();
        Orchestrator = new Orchestrator(Storage, Recorder, Runner, Tools, options ?? new OrchestratorOptions { Clock = static () => Today });
    }

    /// <summary>A fixed today, so that "last year" in a scenario means 2025.</summary>
    public static readonly DateTimeOffset Today = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public Orchestrator Orchestrator { get; }

    /// <summary>A CSV as a read file, the way an attachment arrives.</summary>
    public static ParsedFile Csv(string name, string text) =>
        new SeparatedValuesParser().Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), name);

    public Task<TurnOutcome> Say(Ulid? conversation, string text, params ParsedFile[] files) =>
        Orchestrator.HandleAsync(new TurnInput(conversation, text, Files: files));

    public IStorage Storage { get; }
    public ITraceRecorder Recorder { get; }
    public ScriptedModel Model { get; }
    public ToolCatalog Tools { get; }
    public ServiceProvider Services { get; }
    public OperationRunner Runner { get; }

    public void Dispose()
    {
        Services.Dispose();
        (Storage as IDisposable)?.Dispose();
    }
}
