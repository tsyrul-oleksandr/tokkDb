using TokkDb.Assistant.App.Shell;

namespace TokkDb.Assistant.App;

public sealed class App : Application
{
    private readonly MainPage _main;

    public App(MainPage main)
    {
        _main = main;
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_main) { Title = "Storage", Width = 1440, Height = 900, MinimumWidth = 960, MinimumHeight = 600 };
}
