using Microsoft.Extensions.Logging;
using TokkDb.LLM.Application.Chats;
using TokkDb.LLM.Application.Databases;
using TokkDb.LLM.Application.Diagnostics;
using TokkDb.LLM.Application.Settings;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Application;

public partial class AppShell : Shell
{
    private readonly ILogger<AppShell> _logger;

    public AppShell(
        ChatPage chatPage,
        DatabasePage databasePage,
        SettingsPage settingsPage,
        DiagnosticsPage diagnosticsPage,
        IRecordNavigationService recordNavigation,
        ILogger<AppShell> logger)
    {
        _logger = logger;

        InitializeComponent();
        ChatContent.Content = chatPage;
        DatabaseContent.Content = databasePage;
        SettingsContent.Content = settingsPage;
        DiagnosticsContent.Content = diagnosticsPage;

        // Switching page is the shell's concern; selecting the record is the
        // database view model's. Both react to the same navigation request.
        recordNavigation.RecordNavigationRequested += (_, _) => NavigateToDatabase();
    }

    private void NavigateToDatabase()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await GoToAsync("//database");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not navigate to the database page.");
            }
        });
    }
}
