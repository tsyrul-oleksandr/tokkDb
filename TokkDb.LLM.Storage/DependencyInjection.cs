using Microsoft.Extensions.DependencyInjection;

namespace TokkDb.LLM.Storage;

public static class DependencyInjection
{
    public static IServiceCollection AddStorageServices(this IServiceCollection services)
    {
        // In memory, because this assembly defines the contract and the in-memory backend and
        // knows of no database to keep them in. A host running on TokkDb replaces both this
        // and the storage through AddTokkDbStorage, which points the registry at the
        // _semanticTypes collection of the database it opened (D-4).
        services.AddSingleton<ISemanticTypeStore, InMemorySemanticTypeStore>();
        services.AddSingleton<ISemanticTypeRegistry>(provider =>
            new SemanticTypeRegistry(provider.GetRequiredService<ISemanticTypeStore>()));
        services.AddSingleton<IDisplayRuleEvaluator, DisplayRuleEvaluator>();
        services.AddSingleton<IDisplayRuleValidator, DisplayRuleValidator>();
        services.AddSingleton<IRecordDisplayService, RecordDisplayService>();
        services.AddSingleton<IRecordQueryBinder, RecordQueryBinder>();
        services.AddSingleton<MemoryStorage>();
        services.AddSingleton<IStorage>(provider => provider.GetRequiredService<MemoryStorage>());
        return services;
    }
}
