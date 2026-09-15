using Microsoft.Extensions.Logging;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Application.Chat;
using TokkDb.Assistant.Application.Live;
using TokkDb.Assistant.Application.Settings;
using TokkDb.Assistant.Application.Shell;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Application;

/// <summary>
/// The composition root (step 5.1): the one database file in the user's application data
/// (D-11, NF-4e), the storage and its recorder, the model over the native transport under the
/// egress mode (NF-4), the runner, the orchestrator, and the window. It is the only place the
/// chat client is registered, and the runner the only place it is resolved (AG-1f).
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", Theme.Regular);
                fonts.AddFont("OpenSans-Semibold.ttf", Theme.Semibold);
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var settings = AppSettings.Load();
        builder.Services.AddSingleton(settings);

        // One file, opened once, under the single-writer lock (AG-8a). A second instance is told
        // the storage is in use rather than allowed to corrupt the first.
        builder.Services.AddSingleton(_ => OpenStorage(AppSettings.DatabasePath));
        builder.Services.AddSingleton<IStorage>(provider => provider.GetRequiredService<TokkDbStorage>());
        builder.Services.AddSingleton(provider => new LiveTraceRecorder(provider.GetRequiredService<TokkDbStorage>().Traces));
        builder.Services.AddSingleton<ITraceRecorder>(provider => provider.GetRequiredService<LiveTraceRecorder>());
        builder.Services.AddSingleton(provider => new ToolCatalog(provider.GetRequiredService<IStorage>()));

        // The transport, under the mode: a remote endpoint in LocalOnly is refused here, at
        // composition, with a message naming the mode - not warned about (NF-4).
        builder.Services.AddOllama(settings.ModelEndpoint, settings.Egress);
        builder.Services.AddAssistantAgents(new ModelSettings(settings.Configuration));

        builder.Services.AddSingleton(provider => new Orchestrator(
            provider.GetRequiredService<IStorage>(),
            provider.GetRequiredService<ITraceRecorder>(),
            provider.GetRequiredService<OperationRunner>(),
            provider.GetRequiredService<ToolCatalog>(),
            new OrchestratorOptions { Retention = settings.Retention }));

        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<Browse.BrowseViewModel>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }

    private static TokkDbStorage OpenStorage(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new TokkDbStorage(path);
    }
}
