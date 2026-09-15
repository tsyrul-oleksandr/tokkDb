using System.Collections.Specialized;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Orchestration;
using Shown = TokkDb.Assistant.Agents.Orchestration.Values;

namespace TokkDb.Assistant.Application.Chat;

/// <summary>
/// The conversation (UI-1, UI-3, UI-4, UI-5): what was said, on the left and the right; the
/// question card where one is open; the records a reply showed, rendered here and never by a
/// model (D-6); the composer with its attachments and what was understood about them; and the
/// reply growing while the request runs.
/// </summary>
public sealed partial class ConversationView : Grid
{
    private readonly ChatViewModel _model;
    private readonly VerticalStackLayout _messages;
    private readonly ScrollView _scroll;
    private readonly Editor _composer;
    private readonly VerticalStackLayout _attachments;
    private readonly Label _busy;
    private readonly Button _send;

    public ConversationView(ChatViewModel model)
    {
        _model = model;
        BackgroundColor = Theme.Window;
        RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)];

        _messages = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 20) };
        _scroll = new ScrollView { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Default };
        SemanticProperties.SetDescription(_scroll, "The conversation");
        this.Add(_scroll, 0, 0);

        _attachments = new VerticalStackLayout { Spacing = 6 };

        _composer = new Editor
        {
            Placeholder = "Say what you want, or drop a file here",
            PlaceholderColor = Theme.Dimmer,
            TextColor = Theme.Text,
            BackgroundColor = Theme.Panel,
            FontFamily = Theme.Regular,
            FontSize = Theme.Body,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 44,
            MaximumHeightRequest = 160
        };
        SemanticProperties.SetDescription(_composer, "What you want to say");
        _composer.TextChanged += (_, e) => _model.Draft = e.NewTextValue ?? "";
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.Draft) && _composer.Text != _model.Draft) _composer.Text = _model.Draft;
            if (e.PropertyName == nameof(ChatViewModel.Busy)) UpdateBusy();
        };

        var attach = Theme.Action("Attach");
        SemanticProperties.SetDescription(attach, "Attach a file");
        attach.Clicked += async (_, _) => await PickAsync();

        var paste = Theme.Action("Paste table");
        SemanticProperties.SetDescription(paste, "Paste a table from the clipboard");
        paste.Clicked += async (_, _) =>
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (!await _model.PasteAsync(text)) await Show("Nothing on the clipboard reads as a table.");
        };

        _send = Theme.Action("Send", primary: true);
        SemanticProperties.SetDescription(_send, "Send");
        _send.Clicked += async (_, _) => await _model.SendAsync();

        var cancel = Theme.Action("Stop");
        cancel.Clicked += (_, _) => _model.Cancel();
        cancel.IsVisible = false;

        _busy = Theme.Text11("", Theme.Violet);
        _busy.IsVisible = false;

        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        buttons.Add(_busy);
        buttons.Add(cancel);
        buttons.Add(attach);
        buttons.Add(paste);
        buttons.Add(_send);

        var composerBox = new VerticalStackLayout { Spacing = 10 };
        composerBox.Add(_attachments);
        composerBox.Add(_composer);
        composerBox.Add(buttons);

        var frame = Theme.Card(composerBox, Theme.Panel, Theme.Border, new Thickness(14, 12));
        frame.Margin = new Thickness(24, 0, 24, 20);
        SemanticProperties.SetDescription(frame, "Where you say things; a file dropped here is read before it is sent");

        // Drop (UI-3): a file dropped anywhere on the composer is read and understood before it is sent.
        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Copy;
        drop.Drop += async (_, e) =>
        {
            e.Handled = true;
            var paths = await DroppedFilesAsync(e);
            if (paths.Count > 0) await _model.AttachAsync(paths);
            else if (await e.Data.GetTextAsync() is { } text && !await _model.PasteAsync(text)) await Show("That did not read as a file or a table.");
        };
        frame.GestureRecognizers.Add(drop);

        this.Add(frame, 0, 1);

        _model.Messages.CollectionChanged += OnMessagesChanged;
        _model.Attachments.CollectionChanged += (_, _) => RenderAttachments();
        _model.MessagesChanged += () => MainThread.BeginInvokeOnMainThread(async () =>
        {
            RenderMessages();
            await Task.Delay(50);
            await _scroll.ScrollToAsync(0, _messages.Height + 1000, animated: true);
        });
        _model.Failed += async message => await MainThread.InvokeOnMainThreadAsync(() => Show(message));

        UpdateBusy();
        _ = Task.Run(() => MainThread.BeginInvokeOnMainThread(RenderMessages));

        Loaded += (_, _) => _composer.Focus();
    }

    /// <summary>The file paths a drop carried, read by the platform (partial, per platform).</summary>
    private partial Task<IReadOnlyList<string>> DroppedFilesAsync(DropEventArgs e);

    private async Task PickAsync()
    {
        try
        {
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Choose a file to keep things from",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.MacCatalyst] = ["public.comma-separated-values-text", "public.plain-text", "org.openxmlformats.spreadsheetml.sheet", "org.openxmlformats.wordprocessingml.document", "public.text"],
                    [DevicePlatform.WinUI] = [".csv", ".tsv", ".txt", ".md", ".xlsx", ".xlsm", ".docx", ".docm"]
                })
            });

            // What was chosen is read through the picker's own grant and kept as a copy in the
            // application's cache: the path the sandbox handed over is scoped to that grant, and
            // the copy is what the turn reads and the conversation names.
            var copies = new List<string>();
            var directory = Path.Combine(FileSystem.CacheDirectory, "chosen");
            Directory.CreateDirectory(directory);
            foreach (var result in results)
            {
                var copy = Path.Combine(directory, $"{DateTime.Now:HHmmss}-{result.FileName}");
                await using var source = await result.OpenReadAsync();
                await using var target = File.Create(copy);
                await source.CopyToAsync(target);
                copies.Add(copy);
            }

            await _model.AttachAsync(copies);
        }
        catch (Exception failure)
        {
            try { File.AppendAllText(Path.Combine(FileSystem.AppDataDirectory, "errors.log"), $"{DateTimeOffset.Now:O} choosing a file{Environment.NewLine}{failure}{Environment.NewLine}{Environment.NewLine}"); } catch (IOException) { }
            await Show("The file could not be chosen: " + failure.Message);
        }
    }

    private void UpdateBusy()
    {
        _busy.IsVisible = _model.Busy;
        _busy.Text = _model.Busy ? (_model.BusyText ?? "working") + "…" : "";
        _send.IsEnabled = !_model.Busy;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => MainThread.BeginInvokeOnMainThread(RenderMessages);

    private void RenderMessages()
    {
        _messages.Clear();
        foreach (var message in _model.Messages) _messages.Add(Render(message));
    }

    private View Render(MessageItem message)
    {
        if (message.FromPerson)
        {
            var bubble = Theme.Card(Theme.Text13(message.Text, Theme.Bright), Theme.Hover, Theme.Hover, new Thickness(14, 12));
            var column = new VerticalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, MaximumWidthRequest = 460 };
            if (message.Text.Length > 0) column.Add(bubble);
            foreach (var attachment in message.Attachments)
            {
                column.Add(Theme.Card(Theme.Text12(attachment, Theme.Text), Theme.Panel, Theme.Border, new Thickness(12, 8)));
            }

            if (message.Text.Length > 0) column.Add(CopyButton(() => message.Text, "Copy what you said", LayoutOptions.End));
            SemanticProperties.SetDescription(column, "You said: " + message.Text);
            return column;
        }

        var reply = new VerticalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.Start, MaximumWidthRequest = 520 };

        if (message.Progress is { } progress)
        {
            var live = Theme.Text12(progress, Theme.Violet);
            message.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MessageItem.Progress)) MainThread.BeginInvokeOnMainThread(() => { live.Text = message.Progress ?? ""; live.IsVisible = message.Progress is not null; });
            };
            reply.Add(live);
        }

        if (message.Text.Length > 0 && !(message.Question is not null && message.QuestionOpen))
        {
            var text = Theme.Text13(message.Text);
            message.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MessageItem.Text)) MainThread.BeginInvokeOnMainThread(() => text.Text = message.Text);
            };
            reply.Add(text);
        }

        if (message.Question is { } card && message.QuestionOpen)
        {
            reply.Add(RenderQuestion(message, card));
        }

        if (message.Results is { } page && page.Records.Count > 0)
        {
            reply.Add(RenderResults(page));
        }

        var links = new HorizontalStackLayout { Spacing = 14 };
        if (message.RequestId is { } request)
        {
            var open = new Button { Text = "what happened →", FontFamily = Theme.Regular, FontSize = Theme.Tiny, TextColor = Theme.Violet, BackgroundColor = Theme.Window, Padding = new Thickness(0), CornerRadius = 0, HorizontalOptions = LayoutOptions.Start, MinimumHeightRequest = 18 };
            open.Clicked += (_, _) => _model.Watch(request);
            SemanticProperties.SetDescription(open, "Show what happened for this reply");
            links.Add(open);
        }

        if (message.Text.Length > 0 || message.Results is not null)
        {
            links.Add(CopyButton(() => ReplyAsText(message), "Copy this reply", LayoutOptions.Start));
        }

        if (links.Count > 0) reply.Add(links);

        SemanticProperties.SetDescription(reply, "The assistant said: " + message.Text);
        return reply;
    }

    /// <summary>The confirmation card (UI-4): the loss in counts and examples, and whether it can be undone.</summary>
    private View RenderQuestion(MessageItem message, ConfirmationCard card)
    {
        var body = new VerticalStackLayout { Spacing = 8 };
        body.Add(Theme.Text12(card.Title, Theme.Bright, bold: true));
        foreach (var line in card.Lines) body.Add(Theme.Text12(line, Theme.Text));

        if (card.Examples.Count > 0)
        {
            var examples = new VerticalStackLayout { Spacing = 4 };
            examples.Add(Theme.Text11("For example", Theme.Dim));
            foreach (var example in card.Examples) examples.Add(Theme.Text12(example, Theme.Text));
            body.Add(Theme.Card(examples, Theme.Window, Theme.Line, new Thickness(12, 8)));
        }

        if (card.UndoNote is { } note)
        {
            body.Add(Theme.Text11(note, card.CanBeUndone ? Theme.Dim : Theme.Loss));
        }

        var yes = Theme.Action(card.Yes, primary: false);
        yes.BackgroundColor = card.CanBeUndone ? Theme.DeepViolet : Theme.DeepLoss;
        yes.TextColor = Theme.Bright;
        var no = Theme.Action(card.No);
        // Focus goes back to the composer once the question is answered (UI-7).
        yes.Clicked += async (_, _) => { await _model.AnswerAsync(message, true); _composer.Focus(); };
        no.Clicked += async (_, _) => { await _model.AnswerAsync(message, false); _composer.Focus(); };
        SemanticProperties.SetDescription(yes, card.Yes);
        SemanticProperties.SetDescription(no, card.No);

        var buttons = new HorizontalStackLayout { Spacing = 8 };
        buttons.Add(no);
        buttons.Add(yes);
        body.Add(buttons);

        var frame = Theme.Card(body, Theme.Panel, Theme.Border, new Thickness(16, 14));
        SemanticProperties.SetDescription(frame, "A question: " + card.Title);
        return frame;
    }

    /// <summary>The records a reply showed, as a small table: the display value leads (BR-2).</summary>
    private View RenderResults(ResultPage page)
    {
        var rows = new VerticalStackLayout { Spacing = 4 };
        rows.Add(Theme.Text11($"{page.Thing.Replace('_', ' ')} · {page.Total} in all" + (page.Total > page.Records.Count ? $", the first {page.Records.Count} shown" : ""), Theme.Dim));

        var definition = _model_definition(page.Thing);
        var columns = definition?.Columns.Where(static column => column.Name != Storage.Fingerprints.ColumnName).Select(static column => column.Name).Take(5).ToList() ?? [];

        for (var i = 0; i < page.Records.Count; i++)
        {
            var record = page.Records[i];
            var line = new HorizontalStackLayout { Spacing = 12 };
            line.Add(Theme.Text12(page.Titles[i], Theme.Bright));
            foreach (var column in columns)
            {
                if (record[column] is { } value && Shown.Show(value) != page.Titles[i]) line.Add(Theme.Text12(Shown.Show(value), Theme.Dim));
            }

            // A row opens in the browser (BR-9): by a tap on the row, or by its button from the keyboard.
            var open = new TapGestureRecognizer();
            var (thing, id) = (page.Thing, record.Id);
            open.Tapped += (_, _) => _model.OpenInBrowser(thing, id);
            line.GestureRecognizers.Add(open);
            var openButton = new Button { Text = "›", FontFamily = Theme.Semibold, FontSize = Theme.Small, TextColor = Theme.Violet, BackgroundColor = Theme.Panel, Padding = new Thickness(6, 0), CornerRadius = 4, MinimumHeightRequest = 20 };
            openButton.Clicked += (_, _) => _model.OpenInBrowser(thing, id);
            SemanticProperties.SetDescription(openButton, $"Open {page.Titles[i]} in the browser");
            line.Add(openButton);
            SemanticProperties.SetDescription(line, $"{page.Titles[i]}; opens it in the browser");

            rows.Add(line);
        }

        return Theme.Card(rows, Theme.Panel, Theme.Line, new Thickness(12, 10));
    }

    private Storage.CollectionDefinition? _model_definition(string thing) => _model.Storage.GetCollectionDefinition(thing);

    /// <summary>A reply as text for the clipboard: what was said, the card's lines, and the records shown, one per line.</summary>
    private string ReplyAsText(MessageItem message)
    {
        var lines = new List<string>();
        if (message.Text.Length > 0) lines.Add(message.Text);
        if (message.Question is { } card && message.QuestionOpen)
        {
            lines.Add(card.Title);
            lines.AddRange(card.Lines);
            if (card.UndoNote is { } note) lines.Add(note);
        }

        if (message.Results is { } page)
        {
            var definition = _model_definition(page.Thing);
            var columns = definition?.Columns.Where(static column => column.Name != Storage.Fingerprints.ColumnName).Select(static column => column.Name).ToList() ?? [];
            lines.Add(string.Join("\t", columns));
            foreach (var record in page.Records) lines.Add(string.Join("\t", columns.Select(column => Shown.Show(record[column]))));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A small "copy" that puts the text on the clipboard and says so for a moment.</summary>
    public static Button CopyButton(Func<string> text, string description, LayoutOptions alignment)
    {
        var copy = new Button { Text = "copy", FontFamily = Theme.Regular, FontSize = Theme.Tiny, TextColor = Theme.Dim, BackgroundColor = Colors.Transparent, Padding = new Thickness(0), CornerRadius = 0, HorizontalOptions = alignment, MinimumHeightRequest = 18 };
        copy.Clicked += async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(text());
            copy.Text = "copied";
            await Task.Delay(1200);
            copy.Text = "copy";
        };
        SemanticProperties.SetDescription(copy, description);
        return copy;
    }

    private void RenderAttachments()
    {
        _attachments.Clear();
        foreach (var attachment in _model.Attachments)
        {
            var body = new VerticalStackLayout { Spacing = 3 };
            var head = new HorizontalStackLayout { Spacing = 10 };
            head.Add(Theme.Text12(attachment.Name, Theme.Bright, bold: true));
            var remove = new Label { Text = "remove", FontFamily = Theme.Regular, FontSize = Theme.Tiny, TextColor = Theme.Dim };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _model.Detach(attachment);
            remove.GestureRecognizers.Add(tap);
            head.Add(remove);
            body.Add(head);
            foreach (var line in attachment.Understood) body.Add(Theme.Text11(line, Theme.Text));
            body.Add(Theme.Text11(attachment.WhatHappensNext, Theme.Violet));
            SemanticProperties.SetDescription(body, $"Attached {attachment.Name}: {string.Join(". ", attachment.Understood)}. {attachment.WhatHappensNext}");
            _attachments.Add(Theme.Card(body, Theme.Window, Theme.Border, new Thickness(12, 8)));
        }
    }

    private Task Show(string text) =>
        Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page?.DisplayAlertAsync("Storage", text, "OK") ?? Task.CompletedTask;
}
