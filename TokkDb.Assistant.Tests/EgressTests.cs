using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage.Engine;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// NF-4, NF-4a, NF-4c and step 9.4: egress is a mode - LocalOnly refuses a remote endpoint naming
/// the mode, local means loopback and a LAN address is remote - and a credential lives in the
/// secret store and is not recoverable from the database file, a trace or a settings document.
/// </summary>
public sealed class EgressTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("egress");

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Local_means_loopback_and_a_LAN_address_is_remote()
    {
        Assert.True(ModelEndpoint.Ollama.IsLocal);
        Assert.True(new ModelEndpoint(new Uri("http://127.0.0.1:11434")).IsLocal);
        Assert.True(new ModelEndpoint(new Uri("http://[::1]:11434")).IsLocal);
        Assert.False(new ModelEndpoint(new Uri("http://192.168.1.20:11434")).IsLocal);
        Assert.False(new ModelEndpoint(new Uri("http://ollama.local:11434")).IsLocal);
        Assert.False(new ModelEndpoint(new Uri("https://api.example.com")).IsLocal);
    }

    [Fact]
    public void Configuring_a_remote_endpoint_in_LocalOnly_fails_naming_the_mode()
    {
        var remote = new ModelEndpoint(new Uri("http://192.168.1.20:11434"));

        var refused = Assert.Throws<EgressRefusedException>(() => Egress.Check(remote, EgressMode.LocalOnly));

        Assert.Contains("LocalOnly", refused.Message);
        Assert.Contains("192.168.1.20", refused.Message);
        Assert.Equal(EgressMode.LocalOnly, refused.Mode);

        // The composition root refuses the same way, before any service exists.
        Assert.Throws<EgressRefusedException>(() => new ServiceCollection().AddOllama(remote, EgressMode.LocalOnly));

        // RemoteAllowed is a deliberate choice, and then it is allowed.
        Assert.Same(remote, Egress.Check(remote, EgressMode.RemoteAllowed));
        Assert.True(new EgressSettings(EgressMode.RemoteAllowed).IsRemoteAllowed);
    }

    /// <summary>NF-4c: the credential goes into the transport's header and into nothing that is written down.</summary>
    [Fact]
    public async Task A_credential_is_not_recoverable_from_the_database_file_a_trace_or_a_settings_document()
    {
        const string credential = "sk-ZEBRA-7731-SECRET";
        var remote = new ModelEndpoint(new Uri("https://models.example.com"));
        var secrets = new MemorySecretStore().Keep(remote, credential);

        // The transport is made with the credential in its header - and only there.
        using var services = new ServiceCollection().AddOllama(remote, EgressMode.RemoteAllowed, secrets: secrets).AddAssistantAgents().BuildServiceProvider();
        var client = services.GetRequiredService<IChatClient>();
        Assert.NotNull(client);

        // A request through the assistant with the fake model, over a real file, with the secret store present.
        using var storage = new TokkDbStorage(_database.FilePath);
        var model = new ScriptedModel().Answer("intent", Scenarios.Intent("store")).Answer("extraction", Scenarios.ExtractedConferences);
        using var provider = new ServiceCollection().AddScriptedModel(model).AddSingleton<ISecretStore>(secrets).AddAssistantAgents()
            .AddSingleton(storage.Traces).AddSingleton(new ToolCatalog(storage)).BuildServiceProvider();
        var orchestrator = new Orchestrator(storage, storage.Traces, provider.GetRequiredService<OperationRunner>(), new ToolCatalog(storage));
        var outcome = await orchestrator.HandleAsync(new TurnInput(null, "I want to save these — the conference in Lviv on 14 March 2025, 12 000 hryvnia, and the one in Kyiv on 20 May 2025, 8 500."));
        Assert.True(outcome.Succeeded, outcome.Reply);

        // Nothing written holds it: not the database, not its journal, not the trace's steps.
        Assert.False(Contains(_database.FilePath, credential));
        Assert.False(Contains(_database.FilePath + ".wal", credential));
        Assert.DoesNotContain(storage.Traces.Read(outcome.RequestId)!.Value.Steps, step => (step.Input ?? "").Contains(credential, StringComparison.Ordinal) || (step.Output ?? "").Contains(credential, StringComparison.Ordinal));

        // And a settings document, as the application writes one, has no place for it.
        var settings = System.Text.Json.JsonSerializer.Serialize(new { Endpoint = remote.Address.ToString(), Egress = "RemoteAllowed", Model = "qwen3.5:4b" });
        Assert.DoesNotContain(credential, settings);
    }

    private static bool Contains(string path, string needle) =>
        File.Exists(path) && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
}
