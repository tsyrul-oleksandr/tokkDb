using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Application.Live;
using TokkDb.Assistant.Application.Settings;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Application.Chat;

/// <summary>One conversation in the list (UI-1), most recently active first (SC-10).</summary>
public sealed class ConversationItem(Conversation conversation) : Bindable
{
    public Ulid Id { get; } = conversation.Id;
    public string Title { get; } = conversation.Title;
    public string When { get; } = Ago(conversation.LastActivity);
    public DateTimeOffset LastActivity { get; } = conversation.LastActivity;

    private static string Ago(DateTimeOffset moment)
    {
        var age = DateTimeOffset.UtcNow - moment;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 7 ? moment.LocalDateTime.ToString("dddd")
            : moment.LocalDateTime.ToString("d MMM");
    }
}

/// <summary>One thing said, with what came with it: attachments, a question, records shown.</summary>
public sealed class MessageItem : Bindable
{
    private string _text = "";
    private string? _progress;

    public required bool FromPerson { get; init; }
    public Ulid? RequestId { get; init; }
    public IReadOnlyList<string> Attachments { get; init; } = [];

    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>What is happening while the reply is being made (UI-5): the steps as they complete.</summary>
    public string? Progress { get => _progress; set => Set(ref _progress, value); }

    public ConfirmationCard? Question { get; set; }
    public bool QuestionOpen { get; set; }
    public ResultPage? Results { get; set; }
}

/// <summary>One step of the request being looked at: the semantic list of the diagram (UI-8).</summary>
public sealed class StepItem(ExecutionStep step) : Bindable
{
    public ExecutionStep Step { get; } = step;
    public string Name => Step.Name;
    public string Status => Step.Status switch
    {
        StepStatus.Running => "running",
        StepStatus.Completed => Step.Took is { } took ? $"{took.TotalSeconds:0.0}s" : "done",
        StepStatus.Failed => "failed",
        StepStatus.Interrupted => "interrupted",
        _ => ""
    };
    public string Outcome => Step.Output ?? Step.Input ?? "";
    public bool IsModelCall => Step.Call is not null;
}

/// <summary>A file attached and understood, before anything is stored (UI-3, step 5.2).</summary>
public sealed class AttachmentItem : Bindable
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required ParsedFile File { get; init; }
    public required IReadOnlyList<string> Understood { get; init; }
    public required string WhatHappensNext { get; init; }
}

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The conversation surface (UI-1, UI-3, UI-5, D-15): the conversations, the one open, what was
/// said, the question waiting for an answer, the files attached and what was understood about
/// them, and the steps of the request being looked at, growing as they are recorded.
/// </summary>
public sealed class ChatViewModel : Bindable
{
    private readonly Orchestrator _orchestrator;
    private readonly IStorage _storage;
    private readonly LiveTraceRecorder _live;
    private readonly AppSettings _settings;
    private ConversationItem? _selected;
    private string _draft = "";
    private bool _busy;
    private string? _busyText;
    private Ulid? _watching;
    private MessageItem? _inProgress;
    private CancellationTokenSource? _cancel;

    public ChatViewModel(Orchestrator orchestrator, IStorage storage, LiveTraceRecorder live, AppSettings settings)
    {
        _orchestrator = orchestrator;
        _storage = storage;
        _live = live;
        _settings = settings;

        _live.StepRecorded += OnStepRecorded;
        _live.RequestMoved += OnRequestMoved;
        ReloadConversations();

        // Startup (AG-8b, N-9, step 9.2): every unfinished request is reconciled - a question left
        // on screen when the process went comes back as a question - and the retention sweep runs,
        // both off the UI thread, and the conversations are read again when they are done.
        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.RecoverAsync();
                Agents.Retention.RetentionSweep.Run(_storage, _orchestrator.Recorder, _orchestrator.Options.Retention, DateTimeOffset.UtcNow);
            }
            catch (Exception failure)
            {
                MainThread.BeginInvokeOnMainThread(() => Failed?.Invoke("Tidying up at startup did not finish: " + failure.Message));
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ReloadConversations();

                // A question left on screen when the process went comes back on screen (AG-8, N-9):
                // the conversation that holds a request waiting for an answer is the one opened.
                var waiting = _orchestrator.Recorder.Unfinished().FirstOrDefault(static request => request.State is RequestState.WaitingForUser);
                var toOpen = waiting is not null
                    ? Conversations.FirstOrDefault(conversation => conversation.Id == waiting.ConversationId)
                    : _selected is { } selected ? Conversations.FirstOrDefault(conversation => conversation.Id == selected.Id) : null;
                if (toOpen is not null) Open(toOpen);
                RequestFinished?.Invoke();
            });
        });
    }

    public IStorage Storage => _storage;
    public ITraceRecorder Recorder => _orchestrator.Recorder;

    public ObservableCollection<ConversationItem> Conversations { get; } = [];
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<StepItem> Steps { get; } = [];
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];

    public string ModelLine => $"{_settings.Model} · {(_settings.Egress is Agents.Models.EgressMode.LocalOnly ? "on this machine" : "may leave this machine")}";
    public bool RemoteAllowed => _settings.Egress is Agents.Models.EgressMode.RemoteAllowed;

    public ConversationItem? Selected => _selected;

    public string Draft { get => _draft; set => Set(ref _draft, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public string? BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    /// <summary>The request whose steps the right-hand pane shows (UI-6).</summary>
    public Ulid? Watching
    {
        get => _watching;
        private set
        {
            _watching = value;
            Raise();
        }
    }

    public event Action? MessagesChanged;
    public event Action<string>? Failed;

    /// <summary>What the person could say next (UI-9), when they asked; empty otherwise.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    public bool Suggesting { get => _suggesting; private set => Set(ref _suggesting, value); }
    private bool _suggesting;
    private CancellationTokenSource? _suggestCancel;

    /// <summary>Asks for suggestions about the conversation so far and what is typed; the options replace any shown before.</summary>
    public async Task SuggestAsync()
    {
        if (Suggesting) return;
        _suggestCancel = new CancellationTokenSource();
        Suggesting = true;
        try
        {
            var set = await Task.Run(() => _orchestrator.SuggestAsync(_selected?.Id, Draft, _suggestCancel.Token));
            Suggestions.Clear();
            foreach (var option in set.Options) Suggestions.Add(option);
        }
        catch (OperationCanceledException)
        {
            // Asked to stop: nothing to show.
        }
        catch (Exception failure)
        {
            Failed?.Invoke("No suggestions this time: " + failure.Message);
        }
        finally
        {
            Suggesting = false;
            _suggestCancel = null;
        }
    }

    /// <summary>The option chosen goes into the composer, in place of what was typed; the rest go away.</summary>
    public void UseSuggestion(string text)
    {
        Draft = text;
        Suggestions.Clear();
    }

    public void DismissSuggestions()
    {
        _suggestCancel?.Cancel();
        Suggestions.Clear();
    }

    /// <summary>A request finished, whatever asked for it: the browser reads the catalogue again (BR-3b).</summary>
    public event Action? RequestFinished;

    /// <summary>A row in a reply was chosen: the browser opens that record (BR-9).</summary>
    public event Action<string, Ulid>? OpenRecordRequested;

    public void OpenInBrowser(string thing, Ulid id) => OpenRecordRequested?.Invoke(thing, id);

    // ---- Conversations --------------------------------------------------------------------------------

    public void ReloadConversations()
    {
        Conversations.Clear();
        foreach (var conversation in _storage.Conversations.All()) Conversations.Add(new ConversationItem(conversation));
    }

    public void NewChat()
    {
        _selected = null;
        Raise(nameof(Selected));
        Messages.Clear();
        Steps.Clear();
        Watching = null;
        MessagesChanged?.Invoke();
    }

    public void Open(ConversationItem item)
    {
        _selected = item;
        Raise(nameof(Selected));
        Messages.Clear();
        Ulid? lastRequest = null;

        foreach (var turn in _storage.Conversations.Turns(item.Id))
        {
            var message = new MessageItem
            {
                FromPerson = turn.Speaker is TurnSpeaker.Person,
                RequestId = turn.RequestId,
                Attachments = turn.Attachments.Select(System.IO.Path.GetFileName).ToList()!,
                Text = turn.Text
            };

            if (turn.Speaker is TurnSpeaker.Assistant && turn.RequestId is { } id)
            {
                lastRequest = id;
                var request = _orchestrator.Recorder.Request(id);
                if (request?.State is RequestState.WaitingForUser && turn.Payload?.Contains("\"question\"", StringComparison.Ordinal) == true)
                {
                    message.QuestionOpen = true;
                }

                if (QueryResultHandle.TryRead(turn.Payload) is { } handle && _storage.GetCollectionDefinition(handle.Thing) is { } definition)
                {
                    message.Results = Rehydrate(handle, definition);
                }
            }

            Messages.Add(message);
        }

        Watch(lastRequest);
        MessagesChanged?.Invoke();
    }

    /// <summary>Selecting an earlier request shows its steps (UI-6).</summary>
    public void Watch(Ulid? requestId)
    {
        Watching = requestId;
        Steps.Clear();
        if (requestId is null) return;

        var read = _orchestrator.Recorder.Read(requestId.Value);
        if (read is null) return;
        foreach (var step in read.Value.Steps) Steps.Add(new StepItem(step));
    }

    // ---- Saying things ---------------------------------------------------------------------------------

    public async Task SendAsync()
    {
        var text = Draft.Trim();

        // A table pasted into the composer goes the way a file goes (UI-3): the rows become an
        // attachment, and only what was said around them is the message.
        if (PastedTables.TrySplit(text, out var said, out var table))
        {
            await PasteAsync(table);
            text = said;
        }

        var files = Attachments.Select(static attachment => attachment.File).ToList();
        var paths = Attachments.Select(static attachment => attachment.Path).ToList();
        if (text.Length == 0 && files.Count == 0) return;

        var attached = Attachments.ToList();
        Draft = "";
        Attachments.Clear();

        Messages.Add(new MessageItem { FromPerson = true, Text = text, Attachments = paths.Select(System.IO.Path.GetFileName).ToList()! });
        _inProgress = new MessageItem { FromPerson = false, Text = "", Progress = "working out what you mean…" };
        Messages.Add(_inProgress);
        MessagesChanged?.Invoke();

        var outcome = await RunAsync(cancellation => _orchestrator.HandleAsync(new TurnInput(_selected?.Id, text, paths.Count > 0 ? paths : null, files.Count > 0 ? files : null), cancellation), "thinking");

        // UI-3a: a turn that failed or was stopped kept nothing, so what was attached comes back
        // to the composer, for the person to say how to keep it and send again.
        if (outcome is null || (!outcome.Succeeded && !outcome.IsWaiting))
        {
            foreach (var item in attached.Where(item => Attachments.All(present => present.Path != item.Path))) Attachments.Add(item);
        }
    }

    public async Task AnswerAsync(MessageItem message, bool yes)
    {
        if (message.RequestId is not { } request) return;
        message.QuestionOpen = false;

        _inProgress = new MessageItem { FromPerson = false, Text = "", Progress = yes ? "doing what was shown…" : "leaving it" };
        Messages.Add(_inProgress);
        MessagesChanged?.Invoke();

        await RunAsync(cancellation => _orchestrator.AnswerAsync(request, yes, cancellation), yes ? "applying" : "declining");
    }

    public void Cancel() => _cancel?.Cancel();

    // ---- The browser's side of things (BR-8, BR-9) -------------------------------------------------------

    /// <summary>
    /// A change made from the browser goes through the conversation (BR-8): what the person did
    /// is said here in words, the card - if one is needed - is asked here, and the trace is the
    /// same as for the change said. No model is called.
    /// </summary>
    public async Task<TurnOutcome?> ChangeFromBrowserAsync(StructuralAction action, string said)
    {
        if (Busy) return null;

        Messages.Add(new MessageItem { FromPerson = true, Text = said });
        _inProgress = new MessageItem { FromPerson = false, Text = "", Progress = "checking what that would do…" };
        Messages.Add(_inProgress);
        MessagesChanged?.Invoke();

        return await RunAsync(cancellation => _orchestrator.ChangeAsync(_selected?.Id, action, said, cancellation), "changing");
    }

    /// <summary>
    /// A record opened in the browser becomes what the next thing said is about (BR-9): the
    /// conversation shows it, as a result would be shown, and the composer is ready for the question.
    /// </summary>
    public bool LookAt(string thing, Ulid id, string title)
    {
        var shown = _orchestrator.LookAt(_selected?.Id, thing, id);
        if (shown is null) return false;

        ReloadConversations();
        var item = Conversations.FirstOrDefault(conversation => conversation.Id == shown.Value.Conversation.Id);
        if (item is not null) Open(item);
        Draft = $"About {title}: ";
        return true;
    }

    private async Task<TurnOutcome?> RunAsync(Func<CancellationToken, Task<TurnOutcome>> work, string what)
    {
        Busy = true;
        BusyText = what;
        _cancel = new CancellationTokenSource();
        TurnOutcome? outcome = null;

        try
        {
            outcome = await Task.Run(() => work(_cancel.Token));
            Show(outcome);
        }
        catch (Exception failure)
        {
            if (_inProgress is { } message)
            {
                message.Progress = null;
                message.Text = "Something went wrong: " + failure.Message;
            }

            // The whole failure, for whoever has to find it: the application's own log file, never the conversation.
            try { File.AppendAllText(Path.Combine(FileSystem.AppDataDirectory, "errors.log"), $"{DateTimeOffset.Now:O} {what}{Environment.NewLine}{failure}{Environment.NewLine}{Environment.NewLine}"); } catch (IOException) { }
            Failed?.Invoke(failure.Message);
        }
        finally
        {
            Busy = false;
            BusyText = null;
            _inProgress = null;
            _cancel = null;
            MessagesChanged?.Invoke();
            RequestFinished?.Invoke();
        }

        return outcome;
    }

    private void Show(TurnOutcome outcome)
    {
        if (_inProgress is { } message)
        {
            message.Progress = null;
            message.Text = outcome.Reply;
            message.Question = outcome.Question;
            message.QuestionOpen = outcome.IsWaiting;
            message.Results = outcome.Results;
        }

        if (_selected is null || _selected.Id != outcome.ConversationId)
        {
            ReloadConversations();
            _selected = Conversations.FirstOrDefault(conversation => conversation.Id == outcome.ConversationId);
            Raise(nameof(Selected));
        }
        else
        {
            ReloadConversations();
            _selected = Conversations.FirstOrDefault(conversation => conversation.Id == outcome.ConversationId);
            Raise(nameof(Selected));
        }

        Watch(outcome.RequestId);
    }

    // ---- Attachments (UI-3, step 5.2) ------------------------------------------------------------------

    /// <summary>Reads a file and says what was understood about it, before anything is stored.</summary>
    public async Task AttachAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Attachments.Any(attachment => attachment.Path == path)) continue;
            if (!FileParsers.CanRead(path))
            {
                Failed?.Invoke($"{System.IO.Path.GetFileName(path)} is not a kind of file that can be read here ({string.Join(", ", FileParsers.Extensions)}).");
                continue;
            }

            try
            {
                var item = await Task.Run(() => Understand(path));
                Attachments.Add(item);
            }
            catch (Exception failure)
            {
                Failed?.Invoke($"{System.IO.Path.GetFileName(path)} could not be read: {failure.Message}");
            }
        }
    }

    public void Detach(AttachmentItem item) => Attachments.Remove(item);

    /// <summary>Pasted text that reads as a table becomes a file, so that it goes the same way a dropped one does.</summary>
    public async Task<bool> PasteAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var separator = text.Contains('\t') ? "tab" : text.Contains(';') ? "semicolon" : text.Contains(',') ? "comma" : null;
        if (lines.Length < 2 || separator is null) return false;

        var directory = System.IO.Path.Combine(FileSystem.CacheDirectory, "pasted");
        Directory.CreateDirectory(directory);
        // Named for what it is, since the name is what a new thing made from it is called: "pasted
        // table", which the person can rename by saying "keep these as campaigns".
        var path = System.IO.Path.Combine(directory, $"pasted-table.{(separator == "tab" ? "tsv" : "csv")}");
        await File.WriteAllTextAsync(path, text);
        await AttachAsync([path]);
        return true;
    }

    private AttachmentItem Understand(string path)
    {
        var file = FileParsers.Read(path);
        var understood = new List<string>();
        var next = "";

        if (file.IsTabular)
        {
            foreach (var table in file.Tables)
            {
                var kinds = string.Join(", ", table.Profiles.Select(profile => $"{profile.Name}: {Kind(profile.Inferred)}" + (profile.WasWidened ? $" (mostly {Kind(profile.Majority)})" : "")));
                understood.Add((file.Tables.Count > 1 ? table.Name + ": " : "") + $"{table.RowCount} rows, {table.Columns.Count} headings" +
                               (table.Reading.HeaderWasFound ? $", headings on line {table.Reading.HeaderLineNumber}" : ", no heading row found") +
                               (table.Reading.Preamble.Count > 0 ? $", above the table: \"{string.Join(" / ", table.Reading.Preamble)}\"" : ""));
                understood.Add(kinds);
            }

            var first = file.Tables.FirstOrDefault(static table => table.RowCount > 0);
            if (first is null)
            {
                next = "There is nothing in it to keep.";
            }
            else
            {
                var shortlist = CollectionPrefilter.Shortlist(_storage.GetCollectionDefinitions(), first.Columns, null);
                next = shortlist.Count > 0 && shortlist[0].Score >= 0.6
                    ? $"Looks like it belongs with {shortlist[0].Definition.Name.Replace('_', ' ')} ({string.Join(", ", shortlist[0].MatchedFields)} in common). Nothing is stored until you send."
                    : shortlist.Count > 0
                        ? $"It might belong with {shortlist[0].Definition.Name.Replace('_', ' ')}; that is decided when you send, and you are asked if it is not clear. Nothing is stored until then."
                        : "Nothing you have stored looks like this, so it would become a new thing. Nothing is stored until you send.";
            }
        }
        else if (file.Prose is { } prose)
        {
            understood.Add($"{prose.Blocks.Count} pieces of text: {prose.HeadingCount} headings, {prose.ListItemCount} list items, {prose.TableCount} tables");
            next = "The text will be read for things worth keeping. Nothing is stored until you send.";
        }

        return new AttachmentItem { Path = path, Name = System.IO.Path.GetFileName(path), File = file, Understood = understood, WhatHappensNext = next };
    }

    private static string Kind(ValueKind kind) => SchemaDigest.Kind(SchemaDigest.Type(kind));

    // ---- Live steps (TR-5, UI-5) ------------------------------------------------------------------------

    private void OnStepRecorded(ExecutionStep step)
    {
        // A suggestions call is a request of its own and not a turn (UI-9): its step is traced and
        // budgeted, but "what happened" is about what was said.
        if (step.Name == Agents.Operations.Operations.Suggestions.StepName) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_inProgress is { } message && message.RequestId is null || _inProgress is not null)
            {
                if (_inProgress is { } progress && step.Status is StepStatus.Completed && step.Name is not "the person" and not "the assistant")
                {
                    progress.Progress = step.Name + (string.IsNullOrEmpty(step.Output) ? "…" : ": " + Shorten(step.Output, 80));
                }
            }

            if (_watching is null || _watching == step.RequestId)
            {
                if (_watching is null) Watching = step.RequestId;

                var existing = Steps.FirstOrDefault(item => item.Step.Id == step.Id);
                if (existing is null) Steps.Add(new StepItem(step));
                else Steps[Steps.IndexOf(existing)] = new StepItem(step);
            }
        });
    }

    private void OnRequestMoved(RequestTrace request)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_inProgress is { } message && message.RequestId is null && request.State is RequestState.Running)
            {
                // The request just began: the reply that is being made belongs to it.
                var index = Messages.IndexOf(message);
                if (index >= 0)
                {
                    var bound = new MessageItem { FromPerson = false, RequestId = request.Id, Text = message.Text, Progress = message.Progress };
                    Messages[index] = bound;
                    _inProgress = bound;
                }

                Watch(request.Id);
            }
        });
    }

    private ResultPage? Rehydrate(QueryResultHandle handle, CollectionDefinition definition)
    {
        var records = handle.Shown.Select(shown => _storage.GetById(handle.Thing, shown.Id)).OfType<StorageRecord>().ToList();
        var titles = records.Select(record => DisplayValue.For(definition, record)).ToList();
        return new ResultPage(handle.Thing, records, titles, handle.Count, new Dictionary<string, object?>(),
            new QueryExecutionInfo(QueryAccess.IdentityLookup, handle.Thing, null, "shown again from the identities kept", records.Count, records.Count, 0, 0, TimeSpan.Zero), handle);
    }

    private static string Shorten(string text, int limit) => text.Length <= limit ? text.ReplaceLineEndings(" ") : text[..(limit - 1)].ReplaceLineEndings(" ") + "…";
}
