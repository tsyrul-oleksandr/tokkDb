using TokkDb.Assistant.Agents.Browsing;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Orchestration;
using Shown = TokkDb.Assistant.Agents.Orchestration.Values;
using TokkDb.Assistant.App.Chat;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.App.Browse;

public enum BrowseScreen
{
    Overview,
    Records,
    Record
}

/// <summary>
/// The browser's state (D-13, group BR): which screen is open, the table with its sort, filters
/// and pages, the record being looked at, and the panel saying what a thing keeps. Everything
/// here is read from storage without a model; the two things that change storage - an edit and
/// a removal - go through the conversation's own rules (BR-8) by way of the chat's view model,
/// so the card, the trace and the diagram are the same as for a change said in words.
/// </summary>
public sealed class BrowseViewModel : Bindable
{
    private readonly IStorage _storage;
    private readonly ChatViewModel _chat;
    private RecordTable? _table;
    private RecordDetail? _detail;
    private BrowseScreen _screen = BrowseScreen.Overview;
    private bool _keepsShown;
    private bool _changedUnderneath;
    private bool _editing;
    private bool _loadingMore;
    private string? _notice;

    public BrowseViewModel(IStorage storage, ChatViewModel chat)
    {
        _storage = storage;
        _chat = chat;
        _chat.RequestFinished += OnRequestFinished;
        Reload();
    }

    public IReadOnlyList<ThingCard> Things { get; private set; } = [];
    public BrowseScreen Screen => _screen;
    public RecordTable? Table => _table;
    public RecordDetail? Detail => _detail;
    public bool KeepsShown => _keepsShown;
    public bool ChangedUnderneath => _changedUnderneath;
    public bool Editing => _editing;
    public bool LocalOnly => !_chat.RemoteAllowed;

    /// <summary>One line the surface shows once: where a file went, or what could not be done.</summary>
    public string? Notice { get => _notice; private set => Set(ref _notice, value); }

    /// <summary>The whole surface is drawn again.</summary>
    public event Action? Changed;

    /// <summary>A record is to become the subject of the next thing said (BR-9): thing, identity, title.</summary>
    public event Action<string, Ulid, string>? AskAboutRequested;

    /// <summary>A change from here needs an answer, which is given in the conversation (D-7, BR-8).</summary>
    public event Action? AnswerNeeded;

    // ---- The overview (BR-1) ---------------------------------------------------------------------------

    /// <summary>The overview costs the catalogue and nothing that grows with the records (BR-1a).</summary>
    public void Reload()
    {
        Things = ThingsOverview.Of(_storage);
        Changed?.Invoke();
    }

    public void ShowOverview()
    {
        _screen = BrowseScreen.Overview;
        _detail = null;
        _editing = false;
        Reload();
    }

    public string ThingTitle(string thing) => Things.FirstOrDefault(card => Same(card.Thing, thing))?.Title ?? Capitalise(WhatItKeeps.Plain(thing));

    // ---- The table (BR-2, BR-3, BR-4) ----------------------------------------------------------------

    public void OpenThing(string thing)
    {
        try
        {
            _table = new RecordTable(_storage, thing, pageSize: 50);
        }
        catch (UnknownCollectionException)
        {
            Notice = $"Nothing is kept under the name \"{thing}\".";
            Changed?.Invoke();
            return;
        }

        try
        {
            Notice = _table.Open();
        }
        catch (StorageException failure)
        {
            Notice = failure.Message;
        }

        _screen = BrowseScreen.Records;
        _detail = null;
        _editing = false;
        _changedUnderneath = false;
        Changed?.Invoke();
    }

    /// <summary>A click on a heading: that field, and a second click turns it round (BR-3a: a new sequence each time).</summary>
    public void SortBy(string field)
    {
        if (_table is null) return;
        var descending = _table.Sort is { } sort && Same(sort.ColumnName, field) && !sort.Descending;
        Reopen(new QuerySort(field, descending), _table.Filters);
    }

    /// <summary>A new sequence (BR-3a); a refusal from storage is a line on screen, not a broken table.</summary>
    private void Reopen(QuerySort? sort, IReadOnlyList<TableFilter> filters)
    {
        if (_table is null) return;
        try
        {
            Notice = _table.Open(sort, filters);
        }
        catch (StorageException failure)
        {
            Notice = failure.Message;
        }

        _changedUnderneath = false;
        Changed?.Invoke();
    }

    public string SortWords => _table?.Sort is { } sort
        ? $"{WhatItKeeps.Plain(sort.ColumnName)} {(sort.Descending ? "largest first" : "smallest first")}"
        : "in the order they were kept";

    public void AddFilter(TableFilter filter)
    {
        if (_table is null) return;
        Reopen(_table.Sort, [.. _table.Filters, filter]);
    }

    public void RemoveFilter(TableFilter filter)
    {
        if (_table is null) return;
        Reopen(_table.Sort, [.. _table.Filters.Where(existing => existing != filter)]);
    }

    /// <summary>"Find in conferences": a contains filter on the field a record is named by, replacing the last one.</summary>
    public void Find(string text)
    {
        if (_table is null) return;
        var leading = _table.Columns[0].Name;
        var kept = _table.Filters.Where(filter => !(Same(filter.Field, leading) && filter.Kind is FilterKind.Contains)).ToList();
        if (!string.IsNullOrWhiteSpace(text)) kept.Add(new TableFilter(leading, FilterKind.Contains, text.Trim()));
        Reopen(_table.Sort, kept);
    }

    /// <summary>The next page, once at a time, however often the scroll asks (BR-3).</summary>
    public void More()
    {
        if (_table is null || !_table.HasMore || _loadingMore) return;
        _loadingMore = true;
        try
        {
            Notice = _table.More();
        }
        finally
        {
            _loadingMore = false;
        }

        Changed?.Invoke();
    }

    /// <summary>The offer the changed banner makes (BR-3b): the same sort and filters, from the top.</summary>
    public void Restart()
    {
        if (_table is null) return;
        Reopen(_table.Sort, _table.Filters);
    }

    public string? SaveCsv()
    {
        if (_table is null) return null;
        var directory = Path.Combine(FileSystem.AppDataDirectory, "saved");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{_table.Definition.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(path, _table.ToCsv());
        Notice = $"Saved as {path}";
        Changed?.Invoke();
        return path;
    }

    // ---- What it keeps (BR-6) --------------------------------------------------------------------------

    public void ToggleKeeps()
    {
        _keepsShown = !_keepsShown;
        Changed?.Invoke();
    }

    public IReadOnlyList<FieldInWords> Keeps() => _table is null ? [] : WhatItKeeps.Describe(_table.Definition);

    /// <summary>How the thing connects to the others, as headings a person would write (BR-7).</summary>
    public IReadOnlyList<string> Connections()
    {
        if (_table is null) return [];
        var thing = _table.Definition.Name;
        var lines = new List<string>();
        foreach (var relation in _storage.GetRelations())
        {
            if (Same(relation.FromCollection, thing)) lines.Add($"{WhatItKeeps.Heading(relation, fromThisThing: true)}: {WhatItKeeps.Plain(relation.ToCollection)}.");
            else if (Same(relation.ToCollection, thing)) lines.Add($"{WhatItKeeps.Heading(relation, fromThisThing: false)}: {WhatItKeeps.Plain(relation.FromCollection)}.");
        }

        return lines;
    }

    // ---- The record (BR-5, BR-7) ---------------------------------------------------------------------

    public void OpenRecord(string thing, Ulid id)
    {
        var detail = RecordDetail.Open(_storage, thing, id);
        if (detail is null)
        {
            Notice = "That one is no longer there.";
            Changed?.Invoke();
            return;
        }

        if (_table is null || !Same(_table.Definition.Name, thing))
        {
            _table = new RecordTable(_storage, thing, pageSize: 50);
            _table.Open();
            _changedUnderneath = false;
        }

        _detail = detail;
        _screen = BrowseScreen.Record;
        _editing = false;
        Changed?.Invoke();
    }

    public void CloseRecord()
    {
        _detail = null;
        _editing = false;
        _screen = _table is null ? BrowseScreen.Overview : BrowseScreen.Records;
        Changed?.Invoke();
    }

    /// <summary>The screen that can be drawn: a table screen without a table, or a record screen without a record, is the overview.</summary>
    public BrowseScreen Showing => _screen switch
    {
        BrowseScreen.Record when _detail is null && _table is null => BrowseScreen.Overview,
        BrowseScreen.Record when _detail is null => BrowseScreen.Records,
        BrowseScreen.Records when _table is null => BrowseScreen.Overview,
        var screen => screen
    };

    public void StartEditing()
    {
        _editing = true;
        Changed?.Invoke();
    }

    public void StopEditing()
    {
        _editing = false;
        Changed?.Invoke();
    }

    /// <summary>What the thing last changed, in words, for the line under a record's title.</summary>
    public string LastChangedWords(string thing) => Things.FirstOrDefault(card => Same(card.Thing, thing))?.When ?? "never";

    // ---- Changing things from here (BR-8) -------------------------------------------------------------

    /// <summary>
    /// Each field whose text differs from what is shown becomes one change, read as the field keeps
    /// it; a text that does not read is refused here, before anything runs, and nothing else is
    /// applied either. Applied changes go through the conversation's rules and its trace.
    /// </summary>
    public async Task<bool> SaveEditsAsync(IReadOnlyDictionary<string, string> texts)
    {
        if (_detail is null || _table is null) return false;
        var definition = _table.Definition;
        var changes = new List<(ChangeRecord Action, string Said)>();

        foreach (var field in _detail.Fields)
        {
            if (!texts.TryGetValue(field.Name, out var text)) continue;
            var column = definition.Columns.FirstOrDefault(column => Same(WhatItKeeps.Plain(column.Name), field.Name));
            if (column is null) continue;

            var typed = text.Trim();
            if (typed == field.Value || (typed.Length == 0 && field.IsEmpty)) continue;

            object? value = null;
            if (typed.Length > 0 && !Shown.TryRead(column.Type, typed, out value))
            {
                Notice = $"\"{typed}\" does not read as {WhatItKeeps.Kind(column.Type)}, which is what {field.Name} keeps. Nothing was changed.";
                Changed?.Invoke();
                return false;
            }

            changes.Add((new ChangeRecord(definition.Name, _detail.Id, column.Name, value),
                typed.Length == 0 ? $"Clear the {field.Name} of {_detail.Title}" : $"Change the {field.Name} of {_detail.Title} to {typed}"));
        }

        if (changes.Count == 0)
        {
            _editing = false;
            Changed?.Invoke();
            return true;
        }

        foreach (var (action, said) in changes)
        {
            var outcome = await _chat.ChangeFromBrowserAsync(action, said);
            if (outcome is null || !outcome.Succeeded)
            {
                Notice = outcome?.Reply ?? "That could not be done.";
                break;
            }
        }

        _editing = false;
        RefreshAfterOwnChange();
        return true;
    }

    /// <summary>Removing from here is removing: the same card, the same trace, the same diagram entry (BR-8).</summary>
    public async Task RemoveAsync()
    {
        if (_detail is null) return;
        var (thing, id, title) = (_detail.Thing, _detail.Id, _detail.Title);

        var outcome = await _chat.ChangeFromBrowserAsync(new DeleteRecords(thing, [id]), $"Remove {title} from {WhatItKeeps.Plain(thing)}");
        if (outcome is null) return;

        if (outcome.IsWaiting)
        {
            AnswerNeeded?.Invoke();
            return;
        }

        RefreshAfterOwnChange();
    }

    /// <summary>NF-4d: erasing for good, through the same card and the same trace; the card says conversations are kept.</summary>
    public async Task EraseAsync()
    {
        if (_detail is null) return;
        var (thing, id, title) = (_detail.Thing, _detail.Id, _detail.Title);

        var outcome = await _chat.ChangeFromBrowserAsync(new EraseRecords(thing, [id]), $"Erase {title} from {WhatItKeeps.Plain(thing)} for good");
        if (outcome is null) return;

        if (outcome.IsWaiting)
        {
            AnswerNeeded?.Invoke();
            return;
        }

        RefreshAfterOwnChange();
    }

    // ---- The bridge (BR-9) -----------------------------------------------------------------------------

    public void AskAbout()
    {
        if (_detail is null) return;
        AskAboutRequested?.Invoke(_detail.Thing, _detail.Id, _detail.Title);
    }

    // ---- Keeping up with what changed ------------------------------------------------------------------

    /// <summary>
    /// After any request finishes - said in the conversation or made from here - the overview is
    /// read again, which costs the catalogue; an open table is not re-read but says it has
    /// changed underneath and offers to start again (BR-3b), from the last-changed time BR-1a keeps.
    /// </summary>
    private void OnRequestFinished()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Things = ThingsOverview.Of(_storage);

            if (_table is { } table)
            {
                if (_storage.GetCollectionDefinition(table.Definition.Name) is null)
                {
                    Notice = $"{ThingTitle(table.Definition.Name)} is no longer kept.";
                    _table = null;
                    _detail = null;
                    _screen = BrowseScreen.Overview;
                }
                else if (table.HasChangedUnderneath)
                {
                    _changedUnderneath = true;
                }
            }

            if (_detail is { } detail)
            {
                _detail = RecordDetail.Open(_storage, detail.Thing, detail.Id);
                if (_detail is null)
                {
                    Notice = $"{detail.Title} is no longer there.";
                    _screen = BrowseScreen.Records;
                }
            }

            Changed?.Invoke();
        });
    }

    private void RefreshAfterOwnChange()
    {
        Things = ThingsOverview.Of(_storage);

        if (_table is { } table)
        {
            if (_storage.GetCollectionDefinition(table.Definition.Name) is null)
            {
                _table = null;
                _detail = null;
                _screen = BrowseScreen.Overview;
                Changed?.Invoke();
                return;
            }

            Notice = table.Restart();
            _changedUnderneath = false;
        }

        if (_detail is { } detail)
        {
            _detail = RecordDetail.Open(_storage, detail.Thing, detail.Id);
            if (_detail is null) _screen = BrowseScreen.Records;
        }

        Changed?.Invoke();
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
