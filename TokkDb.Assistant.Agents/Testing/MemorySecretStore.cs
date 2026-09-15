using TokkDb.Assistant.Agents.Models;

namespace TokkDb.Assistant.Agents.Testing;

/// <summary>A secret store in memory, for tests: what the platform's store does, without the platform.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _credentials = new(StringComparer.OrdinalIgnoreCase);

    public MemorySecretStore Keep(ModelEndpoint endpoint, string credential)
    {
        _credentials[endpoint.Address.ToString()] = credential;
        return this;
    }

    public string? Credential(ModelEndpoint endpoint) => _credentials.GetValueOrDefault(endpoint.Address.ToString());
}
