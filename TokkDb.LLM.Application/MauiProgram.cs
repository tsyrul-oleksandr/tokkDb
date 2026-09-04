using Microsoft.Extensions.Logging;
using TokkDb.LLM.Application.Chats;
using TokkDb.LLM.Application.Controls;
using TokkDb.LLM.Application.Databases;
using TokkDb.LLM.Application.Diagnostics;
using TokkDb.LLM.Application.Settings;
using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Orchestration;
using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });
        // Existing .NET logging infrastructure: console for the development
        // console, debug for the IDE output window. No custom logging
        // abstraction is introduced - services consume ILogger<T> from DI.
        // Enables native text selection for chat content.
        SelectableLabel.ConfigureHandler();

        builder.Logging.AddConsole();
#if DEBUG
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#else
        builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif
        // The framework's own chatter would otherwise drown out application logs.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Services.AddCoreServices();
        builder.Services.AddStorageServices();
        builder.Services.AddSingleton<ILlmConfigurationProvider, SettingsLlmConfigurationProvider>();
        builder.Services.AddAgentOrchestration();
        builder.Services.AddSingleton<ISemanticTypeRegistry>(_ =>
            new SemanticTypeRegistry());
        builder.Services.AddSingleton<ISchemaChangeProposalStore>(_ =>
            new MemorySchemaChangeProposalStore());
        builder.Services.AddSingleton(new SchemaToolOptions
        {
            AllowSchemaChanges = true
        });
        builder.Services.AddSingleton<IRecordNavigationService, RecordNavigationService>();
        builder.Services.AddSingleton<IStorageRuntime, StorageRuntime>();
        builder.Services.AddSingleton<IStorageToolGateway, StorageToolGateway>();
        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<ChatPage>();
        builder.Services.AddSingleton<DatabasePage>();
        builder.Services.AddSingleton<DatabaseViewModel>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<DiagnosticsPage>();
        builder.Services.AddTransient<DiagnosticsViewModel>();
        builder.Services.AddSingleton<AppShell>();

        var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        logger.LogInformation(
            "Application startup completed. Version: {Version}, StorageBackend: {StorageBackend}",
            AppInfo.Current.VersionString,
            Settings.Settings.Instance.StorageType);

        return app;
    }
}
