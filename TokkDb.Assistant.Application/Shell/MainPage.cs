using TokkDb.Assistant.App.Browse;
using TokkDb.Assistant.App.Chat;

namespace TokkDb.Assistant.App.Shell;

/// <summary>
/// One window, two surfaces (UI-1, D-13): the conversation, with the list of conversations on
/// the left and "what happened" on the right, the split adjustable and the right pane collapsible;
/// and the browser, reached from the one persistent control in the title bar. The application
/// opens on the conversation.
/// </summary>
public sealed class MainPage : ContentPage
{
    private readonly ChatViewModel _model;
    private readonly Grid _body;
    private readonly ColumnDefinition _rightColumn;
    private readonly Button _chat;
    private readonly Button _browse;
    private readonly View _conversationSurface;
    private readonly View _browserSurface;
    private double _rightWidth = 560;

    public MainPage(ChatViewModel model, BrowseViewModel browse)
    {
        _model = model;
        Title = "Storage";
        BackgroundColor = Theme.Window;

        var root = new Grid { RowDefinitions = [new RowDefinition(48), new RowDefinition(GridLength.Star)], BackgroundColor = Theme.Window };

        // ---- the title bar ----
        var bar = new Grid
        {
            BackgroundColor = Theme.Panel,
            Padding = new Thickness(16, 0),
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 16
        };
        var name = Theme.Text12("Storage", Theme.Bright, bold: true);
        name.VerticalOptions = LayoutOptions.Center;
        bar.Add(name, 0, 0);

        _chat = Toggle("Chat", selected: true);
        _browse = Toggle("Browse", selected: false);
        _chat.Clicked += (_, _) => ShowSurface(chat: true);
        _browse.Clicked += (_, _) => ShowSurface(chat: false);
        SemanticProperties.SetDescription(_chat, "Show the conversation");
        SemanticProperties.SetDescription(_browse, "Browse everything stored");
        var switcher = new HorizontalStackLayout { Spacing = 2, Padding = new Thickness(2), BackgroundColor = Theme.Window, VerticalOptions = LayoutOptions.Center };
        switcher.Add(_chat);
        switcher.Add(_browse);
        bar.Add(Theme.Card(switcher, Theme.Window, Theme.Line, new Thickness(0)), 1, 0);

        var modelLine = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
        modelLine.Add(new BoxView { Color = _model.RemoteAllowed ? Theme.Warn : Theme.Ok, WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, VerticalOptions = LayoutOptions.Center });
        modelLine.Add(Theme.Text11(_model.ModelLine, _model.RemoteAllowed ? Theme.Warn : Theme.Dim));
        SemanticProperties.SetDescription(modelLine, "The model in use: " + _model.ModelLine);
        bar.Add(modelLine, 3, 0);
        root.Add(bar, 0, 0);
        var rule = Theme.Rule();
        rule.VerticalOptions = LayoutOptions.End;
        root.Add(rule, 0, 0);

        // ---- the conversation surface ----
        _rightColumn = new ColumnDefinition(new GridLength(_rightWidth));
        _body = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(232), new ColumnDefinition(GridLength.Star), new ColumnDefinition(6), _rightColumn]
        };

        _body.Add(new ConversationList(_model), 0, 0);
        _body.Add(new ConversationView(_model), 1, 0);

        var splitter = new BoxView { Color = Theme.Line, WidthRequest = 6 };
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            if (e.StatusType is not GestureStatus.Running) return;
            _rightWidth = Math.Clamp(_rightWidth - e.TotalX / 8, 240, 900);
            _rightColumn.Width = new GridLength(_rightWidth);
        };
        splitter.GestureRecognizers.Add(pan);
        var collapse = new TapGestureRecognizer();
        collapse.Tapped += (_, _) => ToggleRight();
        splitter.GestureRecognizers.Add(collapse);
        SemanticProperties.SetDescription(splitter, "Drag to resize what happened; tap to hide or show it");
        _body.Add(splitter, 2, 0);
        _body.Add(new StepsPane(_model), 3, 0);

        _conversationSurface = _body;
        _browserSurface = new BrowseSurface(browse) { IsVisible = false };

        // The two surfaces, connected both ways (BR-9), and a change from the browser that needs
        // an answer is answered where the card is: in the conversation (D-7, BR-8).
        _model.OpenRecordRequested += (thing, id) => { browse.OpenRecord(thing, id); ShowSurface(chat: false); };
        browse.AskAboutRequested += (thing, id, title) => { if (_model.LookAt(thing, id, title)) ShowSurface(chat: true); };
        browse.AnswerNeeded += () => ShowSurface(chat: true);

        var surfaces = new Grid();
        surfaces.Add(_conversationSurface);
        surfaces.Add(_browserSurface);
        root.Add(surfaces, 0, 1);

        Content = root;

        Loaded += async (_, _) => await SelfTest.RunIfAskedAsync(_model, browse, CaptureAsync, chat => ShowSurface(chat));
    }

    /// <summary>The window's own picture of itself, for the self-test: no screen-recording permission is involved.</summary>
    private static async Task CaptureAsync(string path)
    {
        try
        {
            if (!Screenshot.Default.IsCaptureSupported) return;
            var shot = await Screenshot.Default.CaptureAsync();
            await using var file = File.Create(path);
            await using var stream = await shot.OpenReadAsync(ScreenshotFormat.Png);
            await stream.CopyToAsync(file);
        }
        catch (Exception failure)
        {
            // The picture is a convenience; the log is the record - and says why there is no picture.
            try { File.AppendAllText(SelfTest.LogPath, $"screenshot failed: {failure.GetType().Name}: {failure.Message}\n"); } catch (IOException) { }
        }
    }

    private static Button Toggle(string text, bool selected) => new()
    {
        Text = text,
        FontFamily = Theme.Semibold,
        FontSize = Theme.Small,
        TextColor = selected ? Theme.Bright : Theme.Dim,
        BackgroundColor = selected ? Theme.Hover : Theme.Window,
        CornerRadius = 5,
        Padding = new Thickness(14, 4),
        MinimumHeightRequest = 26
    };

    /// <summary>Moving between the two surfaces loses neither's place: both stay built, one is shown (UI-1).</summary>
    private void ShowSurface(bool chat)
    {
        _conversationSurface.IsVisible = chat;
        _browserSurface.IsVisible = !chat;
        _chat.TextColor = chat ? Theme.Bright : Theme.Dim;
        _chat.BackgroundColor = chat ? Theme.Hover : Theme.Window;
        _browse.TextColor = chat ? Theme.Dim : Theme.Bright;
        _browse.BackgroundColor = chat ? Theme.Window : Theme.Hover;
    }

    private void ToggleRight()
    {
        if (_rightColumn.Width.Value > 0)
        {
            _rightColumn.Width = new GridLength(0);
        }
        else
        {
            _rightColumn.Width = new GridLength(_rightWidth);
        }
    }
}
