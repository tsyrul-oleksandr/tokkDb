namespace TokkDb.Assistant.App.Chat;

public sealed partial class ConversationView
{
#if MACCATALYST
    private partial async Task<IReadOnlyList<string>> DroppedFilesAsync(DropEventArgs e)
    {
        var session = e.PlatformArgs?.DropSession;
        if (session is null) return [];

        var completion = new TaskCompletionSource<IReadOnlyList<string>>();
        try
        {
            session.LoadObjects(new ObjCRuntime.Class(typeof(Foundation.NSUrl)), objects =>
                completion.TrySetResult([.. objects.OfType<Foundation.NSUrl>().Select(static url => url.Path).OfType<string>().Where(File.Exists)]));
        }
        catch (Exception)
        {
            return [];
        }

        var done = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        return done == completion.Task ? completion.Task.Result : [];
    }
#elif WINDOWS
    private partial async Task<IReadOnlyList<string>> DroppedFilesAsync(DropEventArgs e)
    {
        var view = e.PlatformArgs?.DragEventArgs.DataView;
        if (view is null || !view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return [];
        var items = await view.GetStorageItemsAsync();
        return [.. items.Select(static item => item.Path).Where(File.Exists)];
    }
#else
    private partial Task<IReadOnlyList<string>> DroppedFilesAsync(DropEventArgs e) => Task.FromResult<IReadOnlyList<string>>([]);
#endif
}
