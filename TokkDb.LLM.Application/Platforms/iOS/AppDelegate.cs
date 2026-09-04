using Foundation;
using TokkDb.LLM.Application;

namespace TokkDb.Application;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
