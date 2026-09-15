namespace TokkDb.Assistant.Agents.Models;

/// <summary>
/// Where a credential lives (NF-4c): the platform's own secret store, and nowhere else - never
/// the database, never a trace, never a settings document. The agents only ever ask for one by
/// the endpoint it belongs to, at the moment the transport is made, and keep no copy.
/// </summary>
public interface ISecretStore
{
    /// <summary>The credential for an endpoint, or null when there is none.</summary>
    string? Credential(ModelEndpoint endpoint);
}

/// <summary>No credentials at all: what a local model needs, and the default.</summary>
public sealed class NoSecrets : ISecretStore
{
    public static readonly NoSecrets Instance = new();

    public string? Credential(ModelEndpoint endpoint) => null;
}
