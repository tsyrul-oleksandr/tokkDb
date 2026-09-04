using Microsoft.Extensions.Logging;

namespace TokkDb.LLM.Application;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly AppShell _mainPage;
    private readonly ILogger<App> _logger;

    public App(AppShell mainPage, ILogger<App> logger)
    {
        _mainPage = mainPage;
        _logger = logger;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new NavigationPage(_mainPage));

        window.Created += (_, _) => _logger.LogInformation("Application window created.");
        window.Activated += (_, _) => _logger.LogDebug("Application window activated.");
        window.Stopped += (_, _) => _logger.LogInformation("Application stopping.");
        window.Destroying += (_, _) => _logger.LogInformation("Application shutdown started.");

        return window;
    }
}
