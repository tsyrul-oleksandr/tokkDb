using Microsoft.Extensions.DependencyInjection;

namespace TokkDb.LLM.Storage;

public static class DependencyInjection
{
    public static IServiceCollection AddStorageServices(this IServiceCollection services)
    {
        services.AddSingleton<ISemanticTypeRegistry, SemanticTypeRegistry>();
        services.AddSingleton<IDisplayRuleEvaluator, DisplayRuleEvaluator>();
        services.AddSingleton<IDisplayRuleValidator, DisplayRuleValidator>();
        services.AddSingleton<IRecordDisplayService, RecordDisplayService>();
        services.AddSingleton<IRecordQueryBinder, RecordQueryBinder>();
        services.AddSingleton<MemoryStorage>();
        services.AddSingleton<FileStorage>();
        services.AddSingleton<IStorage>(provider => provider.GetRequiredService<MemoryStorage>());
        return services;
    }
}
