using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Testing;

namespace TokkDb.Assistant.Agents;

/// <summary>
/// The composition of the orchestration assembly (AG-1f, NF-4).
///
/// The chat client is registered here and resolved in one place, the <see cref="OperationRunner"/>.
/// A composition root chooses the transport - the native Ollama client through
/// <see cref="AddOllama"/>, under the egress mode's rule, or a scripted fake through
/// <see cref="AddScriptedModel"/> - and nothing it registers is handed the client type.
/// </summary>
public static class AssistantServices
{
    /// <summary>The runner and what it needs. The recorder and the tool catalogue come from the storage the root opened.</summary>
    public static IServiceCollection AddAssistantAgents(this IServiceCollection services, ModelSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(settings ?? new ModelSettings());
        services.AddSingleton(provider => new ChatModel(
            provider.GetRequiredService<IChatClient>(),
            provider.GetRequiredService<ModelSettings>()));
        services.AddSingleton<OperationRunner>();

        return services;
    }

    /// <summary>
    /// The native transport (step 0.3): OllamaSharp speaking <c>/api/chat</c>, so that the window
    /// and the output cap are per-call options the runner sets. Refused when the endpoint is not
    /// on this machine and the mode is <see cref="EgressMode.LocalOnly"/> (NF-4).
    /// </summary>
    /// <exception cref="EgressRefusedException">The endpoint is remote and the mode forbids it.</exception>
    public static IServiceCollection AddOllama(this IServiceCollection services, ModelEndpoint endpoint, EgressMode mode, TimeSpan? timeout = null, ISecretStore? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        Egress.Check(endpoint, mode);

        services.AddSingleton(new EgressSettings(mode));
        services.AddSingleton(secrets ?? NoSecrets.Instance);
        services.AddSingleton<IChatClient>(provider =>
        {
            var http = new HttpClient { BaseAddress = endpoint.Address, Timeout = timeout ?? TimeSpan.FromMinutes(5) };

            // NF-4c: a credential is read from the secret store at the moment the transport is
            // made and goes into the request header and nowhere else - not into a setting, a
            // trace or the database, none of which ever see it.
            if (provider.GetRequiredService<ISecretStore>().Credential(endpoint) is { Length: > 0 } credential)
            {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
            }

            return new OllamaApiClient(http, ModelConfiguration.Default.Model);
        });

        return services;
    }

    /// <summary>A model that is not a model (D-12): the scripted fake, for tests and the budget harness.</summary>
    public static IServiceCollection AddScriptedModel(this IServiceCollection services, ScriptedModel model)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(model);

        services.AddSingleton(new EgressSettings(EgressMode.LocalOnly));
        services.AddSingleton<IChatClient>(model);
        return services;
    }
}
