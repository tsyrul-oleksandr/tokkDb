using Microsoft.Extensions.DependencyInjection;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// Wiring for the engine-backed storage. It is here rather than in
/// <c>TokkDb.LLM.Storage</c> because that assembly must not know an engine exists — it
/// defines <see cref="IStorage"/> and the in-memory implementation of it, and a reference the
/// other way would make the contract depend on one of its implementations.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers TokkDb as the storage, with the semantic type registry pointed at it.
    ///
    /// The registry is the reason this is a method rather than two lines at a call site: it
    /// has to be built on the same database the storage opened, and a registry accidentally
    /// left on the in-memory store would keep working, silently, until a restart lost
    /// everything the model had learned.
    /// </summary>
    public static IServiceCollection AddTokkDbStorage(this IServiceCollection services, string databaseFilePath)
    {
        services.AddSingleton(provider => new TokkDbStorage(
            databaseFilePath,
            provider.GetService<TokkDb.LLM.Core.Diagnostics.IDiagnosticsService>()));
        services.AddSingleton<IStorage>(provider => provider.GetRequiredService<TokkDbStorage>());
        services.AddSingleton<ISemanticTypeStore>(provider =>
            provider.GetRequiredService<TokkDbStorage>().SemanticTypes);
        services.AddSingleton<ISemanticTypeRegistry>(provider =>
            new SemanticTypeRegistry(provider.GetRequiredService<ISemanticTypeStore>()));
        return services;
    }
}
