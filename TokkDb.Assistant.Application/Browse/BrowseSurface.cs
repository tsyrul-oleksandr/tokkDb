using TokkDb.Assistant.Agents.Browsing;
using TokkDb.Assistant.Agents.Orchestration;

namespace TokkDb.Assistant.App.Browse;

/// <summary>
/// The browser (D-13, UI-1, group BR): everything stored on the left, and on the right the
/// overview, the table a page at a time, or one record - with the panel saying what a thing
/// keeps beside them. Every word here is a word the person would use (UI-2, BR-6); nothing here
/// asks a model anything.
/// </summary>
public sealed class BrowseSurface : Grid
{
    private const int ColumnsShown = 6;

    private readonly BrowseViewModel _model;
    private readonly VerticalStackLayout _rail;
    private readonly Grid _main;
    private readonly Entry _find;
    private bool _rendering;

    public BrowseSurface(BrowseViewModel model)
    {
        _model = model;
        BackgroundColor = Theme.Window;
        ColumnDefinitions = [new ColumnDefinition(232), new ColumnDefinition(GridLength.Star)];

        // ---- the rail: everything stored, and the way back to the overview ----
        var railBox = new Grid { BackgroundColor = Theme.Panel, RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)] };
        _find = new Entry
        {
            Placeholder = "Find anything you've stored", PlaceholderColor = Theme.Dimmer, TextColor = Theme.Text, BackgroundColor = Theme.Window,
            FontFamily = Theme.Regular, FontSize = Theme.Small, Margin = new Thickness(12, 12, 12, 4)
        };
        _find.Completed += (_, _) => _model.Find(_find.Text ?? "");
        SemanticProperties.SetDescription(_find, "Find, by the name a record is known by");
        railBox.Add(_find, 0, 0);
        _rail = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(8, 4, 8, 8) };
        SemanticProperties.SetDescription(_rail, "Everything you have stored");
        railBox.Add(new ScrollView { Content = _rail }, 0, 1);
        this.Add(railBox, 0, 0);

        _main = new Grid();
        this.Add(_main, 1, 0);

        // Queued rather than inline: a change can finish inside the conversation's own event, and
        // tearing this surface's views down in the middle of that pass is what the platform
        // refuses. After the pass, the rebuild is an ordinary one.
        _model.Changed += () => Dispatcher.Dispatch(Render);
        Render();
    }

    private void Render()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            RenderRail();
            var content = _model.Showing switch
            {
                BrowseScreen.Records => RenderRecords(),
                BrowseScreen.Record => RenderRecord(),
                _ => RenderOverview()
            };
            _main.Clear();
            _main.Add(content);
        }
        catch (Exception failure)
        {
            // A drawing failure is a line on screen, never a broken conversation.
            _main.Clear();
            _main.Add(Theme.Text12("This could not be drawn: " + failure.Message, Theme.Loss));
        }
        finally
        {
            _rendering = false;
        }
    }

    // ---- the rail --------------------------------------------------------------------------------------

    private void RenderRail()
    {
        _rail.Clear();
        _find.IsVisible = _model.Showing is not BrowseScreen.Overview;
        _find.Placeholder = _model.Table is { } table ? $"Find in {WhatItKeeps.Plain(table.Definition.Name)}" : "Find anything you've stored";

        var overview = RailItem("Everything stored", null, _model.Showing is BrowseScreen.Overview);
        overview.Clicked += (_, _) => _model.ShowOverview();
        SemanticProperties.SetDescription(overview, "Everything stored: the overview");
        _rail.Add(overview);

        foreach (var thing in _model.Things)
        {
            var selected = _model.Showing is not BrowseScreen.Overview && _model.Table is { } shown && string.Equals(shown.Definition.Name, thing.Thing, StringComparison.OrdinalIgnoreCase);
            var item = RailItem(thing.Title, thing.HowMany.ToString(), selected);
            var name = thing.Thing;
            item.Clicked += (_, _) => _model.OpenThing(name);
            SemanticProperties.SetDescription(item, $"{thing.Title}, {thing.Count}; opens it");
            _rail.Add(item);
        }
    }

    /// <summary>A rail item is a button (UI-7): reachable by Tab, activated by Space or Return, and named for a screen reader.</summary>
    private static Button RailItem(string title, string? count, bool selected) => new()
    {
        Text = count is null ? title : $"{title}   {count}",
        FontFamily = selected ? Theme.Semibold : Theme.Regular,
        FontSize = Theme.Small,
        TextColor = selected ? Theme.Bright : Theme.Text,
        BackgroundColor = selected ? Theme.Hover : Theme.Panel,
        CornerRadius = 5,
        Padding = new Thickness(10, 6),
        HorizontalOptions = LayoutOptions.Fill,
        LineBreakMode = LineBreakMode.TailTruncation
    };

    // ---- the overview (BR-1) ---------------------------------------------------------------------------

    private View RenderOverview()
    {
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(28, 24) };
        var things = _model.Things;
        var entries = things.Sum(static thing => thing.HowMany);

        page.Add(new Label { Text = "Everything you've stored", FontFamily = Theme.Semibold, FontSize = 18, TextColor = Theme.Bright });
        page.Add(Theme.Text12(things.Count == 0
            ? "Nothing yet. Anything you say in Chat can start something."
            : $"{Plural(things.Count, "kind of thing", "kinds of thing")} · {Plural(entries, "entry", "entries")} altogether · {(_model.LocalOnly ? "nothing leaves this machine" : "the model may be reached over the network")}", Theme.Dim));

        if (things.Count > 0)
        {
            page.Add(Theme.Text11("Recently changed", Theme.Dim));
        }

        foreach (var thing in things)
        {
            var card = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)],
                RowSpacing = 4
            };
            card.Add(new Label { Text = thing.Title, FontFamily = Theme.Semibold, FontSize = 15, TextColor = Theme.Bright }, 0, 0);
            var openButton = Theme.Action($"Open · {thing.Count}");
            var opened = thing.Thing;
            openButton.Clicked += (_, _) => _model.OpenThing(opened);
            SemanticProperties.SetDescription(openButton, $"Open {thing.Title}, {thing.Count}");
            card.Add(openButton, 1, 0);
            card.Add(Theme.Text12(thing.Keeps, Theme.Text), 0, 1);
            card.Add(Theme.Text11($"changed {thing.When}", Theme.Dimmer), 0, 2);

            var frame = Theme.Card(card, Theme.Panel, Theme.Line, new Thickness(16, 12));
            var open = new TapGestureRecognizer();
            var name = thing.Thing;
            open.Tapped += (_, _) => _model.OpenThing(name);
            frame.GestureRecognizers.Add(open);
            SemanticProperties.SetDescription(frame, $"{thing.Title}: {thing.Keeps} {thing.Count}, changed {thing.When}");
            page.Add(frame);
        }

        page.Add(Theme.Text11("Anything you say in Chat can add to these, or start a new one.", Theme.Dimmer));
        if (_model.Notice is { } notice) page.Add(Theme.Text11(notice, Theme.Warn));

        return new ScrollView { Content = page };
    }

    // ---- the table (BR-2, BR-3, BR-4, BR-10) -----------------------------------------------------------

    private View RenderRecords()
    {
        var table = _model.Table!;
        var thing = table.Definition.Name;
        var title = _model.ThingTitle(thing);

        var page = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)] };

        // ---- the head: title, purpose, actions, filters, the count line ----
        var head = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(28, 20, 28, 10) };
        var titleRow = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 8 };
        var heading = new VerticalStackLayout { Spacing = 2 };
        heading.Add(new Label { Text = title, FontFamily = Theme.Semibold, FontSize = 18, TextColor = Theme.Bright });
        heading.Add(Theme.Text12(table.Definition.Purpose ?? $"{title} you keep", Theme.Dim));
        titleRow.Add(heading, 0, 0);

        var keeps = Theme.Action(_model.KeepsShown ? "Hide what this keeps" : "What this keeps");
        keeps.Clicked += (_, _) => _model.ToggleKeeps();
        SemanticProperties.SetDescription(keeps, "Show what each of these keeps, in plain words");
        titleRow.Add(keeps, 1, 0);
        var save = Theme.Action("Save as a file");
        save.Clicked += (_, _) => _model.SaveCsv();
        SemanticProperties.SetDescription(save, "Save what is on screen, with its sort and filter, as a file");
        titleRow.Add(save, 2, 0);
        head.Add(titleRow);

        // Filters, visible and removable (BR-4), and the way to add one.
        var filters = new HorizontalStackLayout { Spacing = 6 };
        foreach (var filter in table.Filters)
        {
            var chip = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(10, 3) };
            chip.Add(Theme.Text12(filter.Describe(), Theme.Bright));
            var remove = Theme.Text12("×", Theme.Dim);
            var drop = new TapGestureRecognizer();
            var which = filter;
            drop.Tapped += (_, _) => _model.RemoveFilter(which);
            chip.GestureRecognizers.Add(drop);
            SemanticProperties.SetDescription(chip, $"Filter: {filter.Describe()}. Tap to remove it");
            chip.Add(remove);
            filters.Add(Theme.Card(chip, Theme.Hover, Theme.Hover, new Thickness(0)));
        }

        filters.Add(RenderNarrow(table));
        head.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Never });

        var countLine = table.Total == 0
            ? (table.Filters.Count > 0 ? "None of them match that" : "Nothing kept here yet")
            : table.Filters.Count > 0
                ? $"{table.Total} of {_model.Things.FirstOrDefault(card => string.Equals(card.Thing, thing, StringComparison.OrdinalIgnoreCase))?.HowMany ?? table.Total} · {_model.SortWords}"
                : $"{Plural(table.Total, "of them", "of them")} · {_model.SortWords}";
        head.Add(Theme.Text12(countLine, Theme.Dim));

        if (_model.ChangedUnderneath)
        {
            var banner = new HorizontalStackLayout { Spacing = 12 };
            banner.Add(Theme.Text12("These have changed since you opened them, so this list may be out of date.", Theme.Warn));
            var again = Theme.Action("Start again");
            again.Clicked += (_, _) => _model.Restart();
            SemanticProperties.SetDescription(again, "Read the list again from the top");
            banner.Add(again);
            head.Add(Theme.Card(banner, Theme.Panel, Theme.Warn, new Thickness(12, 8)));
        }

        if (_model.Notice is { } notice) head.Add(Theme.Text11(notice, Theme.Warn));
        page.Add(head, 0, 0);

        // ---- the rows: headings that sort, rows that open, more as you scroll ----
        var columns = table.Columns.Take(ColumnsShown).ToList();
        var allColumns = table.Columns.ToList();
        var rows = new VerticalStackLayout { Spacing = 0, Padding = new Thickness(28, 0, 28, 12) };
        rows.Add(RenderHeadings(table, columns));
        rows.Add(Theme.Rule());

        foreach (var row in table.Rows)
        {
            var line = new Grid { Padding = new Thickness(8, 7), ColumnSpacing = 12 };
            foreach (var _ in columns) line.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            line.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = new Label
                {
                    Text = row.Cells[allColumns.IndexOf(columns[i])], FontFamily = i == 0 ? Theme.Semibold : Theme.Regular, FontSize = Theme.Small,
                    TextColor = i == 0 ? Theme.Bright : Theme.Text, LineBreakMode = LineBreakMode.TailTruncation
                };
                line.Add(cell, i, 0);
            }

            var open = new TapGestureRecognizer();
            var id = row.Id;
            open.Tapped += (_, _) => _model.OpenRecord(thing, id);
            line.GestureRecognizers.Add(open);
            var openButton = new Button { Text = "›", FontFamily = Theme.Semibold, FontSize = Theme.Small, TextColor = Theme.Violet, BackgroundColor = Theme.Window, Padding = new Thickness(6, 0), CornerRadius = 4, MinimumHeightRequest = 22 };
            openButton.Clicked += (_, _) => _model.OpenRecord(thing, id);
            SemanticProperties.SetDescription(openButton, $"Open {row.Title}");
            line.Add(openButton, columns.Count, 0);
            SemanticProperties.SetDescription(line, $"{row.Title}; opens this one");
            rows.Add(line);
            rows.Add(Theme.Rule());
        }

        var foot = new HorizontalStackLayout { Spacing = 12, Padding = new Thickness(8, 10) };
        foot.Add(Theme.Text11(table.Rows.Count == 0
            ? ""
            : table.HasMore
                ? $"Showing 1–{table.Rows.Count} of {table.Total} · the rest load as you scroll"
                : $"Showing all {table.Rows.Count}", Theme.Dim));
        if (table.HasMore)
        {
            var more = Theme.Action("Show more");
            more.Clicked += (_, _) => _model.More();
            SemanticProperties.SetDescription(more, "Show the next page");
            foot.Add(more);
        }

        rows.Add(foot);

        var scroll = new ScrollView { Content = rows };
        scroll.Scrolled += (_, e) =>
        {
            if (table.HasMore && e.ScrollY + scroll.Height >= scroll.ContentSize.Height - 120) _model.More();
        };

        // The panel beside the rows, when asked for (BR-6).
        var body = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(_model.KeepsShown ? 320 : 0)] };
        body.Add(scroll, 0, 0);
        if (_model.KeepsShown) body.Add(RenderKeeps(table), 1, 0);
        page.Add(body, 0, 1);

        var diagnostics = Theme.Text11("no thinking needed for this — nothing was asked of a model", Theme.Dimmer);
        diagnostics.Padding = new Thickness(28, 6);
        if (table.LastExecution is { } execution) SemanticProperties.SetHint(diagnostics, execution.Description);
        page.Add(diagnostics, 0, 2);

        return page;
    }

    private Grid RenderHeadings(RecordTable table, IReadOnlyList<Storage.ColumnDefinition> columns)
    {
        var headings = new Grid { Padding = new Thickness(8, 6), ColumnSpacing = 12 };
        foreach (var _ in columns) headings.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headings.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var sorted = table.Sort is { } sort && string.Equals(sort.ColumnName, column.Name, StringComparison.OrdinalIgnoreCase);
            var mark = sorted ? (table.Sort!.Descending ? " ▼" : " ▲") : "";
            var label = new Button
            {
                Text = WhatItKeeps.Plain(column.Name).ToUpperInvariant() + mark, FontFamily = Theme.Semibold, FontSize = Theme.Tiny,
                TextColor = sorted ? Theme.Violet : Theme.Dim, BackgroundColor = Theme.Window, Padding = new Thickness(0, 2), CornerRadius = 0,
                HorizontalOptions = LayoutOptions.Start, LineBreakMode = LineBreakMode.TailTruncation
            };
            var name = column.Name;
            label.Clicked += (_, _) => _model.SortBy(name);
            SemanticProperties.SetDescription(label, $"{WhatItKeeps.Plain(column.Name)}; sorts by it, again to turn the order round");
            headings.Add(label, i, 0);
        }

        return headings;
    }

    /// <summary>"Narrow it down": a field, what is asked of it, and the value - the same query the assistant would write (BR-4).</summary>
    private View RenderNarrow(RecordTable table)
    {
        var fields = table.Columns.Select(static column => WhatItKeeps.Plain(column.Name)).ToList();
        var field = new Picker { Title = "field", ItemsSource = fields, TextColor = Theme.Text, TitleColor = Theme.Dimmer, BackgroundColor = Theme.Window, FontSize = Theme.Small, FontFamily = Theme.Regular, WidthRequest = 120 };
        var kind = new Picker { Title = "is", TextColor = Theme.Text, TitleColor = Theme.Dimmer, BackgroundColor = Theme.Window, FontSize = Theme.Small, FontFamily = Theme.Regular, WidthRequest = 110 };
        var value = new Entry { Placeholder = "value", PlaceholderColor = Theme.Dimmer, TextColor = Theme.Text, BackgroundColor = Theme.Window, FontSize = Theme.Small, FontFamily = Theme.Regular, WidthRequest = 140 };
        SemanticProperties.SetDescription(field, "Which field to narrow by");
        SemanticProperties.SetDescription(kind, "How to narrow it");
        SemanticProperties.SetDescription(value, "The value to narrow to");

        var kinds = new List<FilterKind>();
        field.SelectedIndexChanged += (_, _) =>
        {
            if (field.SelectedIndex < 0) return;
            kinds = [.. TableFilter.KindsFor(table.Columns[field.SelectedIndex].Type)];
            kind.ItemsSource = kinds.Select(Words).ToList();
            kind.SelectedIndex = 0;
        };

        var add = Theme.Action("Narrow it down");
        SemanticProperties.SetDescription(add, "Add this as a filter");
        add.Clicked += (_, _) =>
        {
            if (field.SelectedIndex < 0 || kind.SelectedIndex < 0) return;
            var chosen = kinds[kind.SelectedIndex];
            var needsValue = chosen is not (FilterKind.IsEmpty or FilterKind.IsFilled);
            if (needsValue && string.IsNullOrWhiteSpace(value.Text)) return;
            _model.AddFilter(new TableFilter(table.Columns[field.SelectedIndex].Name, chosen, needsValue ? value.Text.Trim() : null));
        };

        var row = new HorizontalStackLayout { Spacing = 6 };
        row.Add(field);
        row.Add(kind);
        row.Add(value);
        row.Add(add);
        return row;
    }

    private static string Words(FilterKind kind) => kind switch
    {
        FilterKind.Is => "is",
        FilterKind.IsNot => "is not",
        FilterKind.Contains => "contains",
        FilterKind.Over => "over",
        FilterKind.Under => "under",
        FilterKind.OnOrAfter => "from",
        FilterKind.OnOrBefore => "up to",
        FilterKind.IsEmpty => "is empty",
        FilterKind.IsFilled => "is filled in",
        _ => kind.ToString().ToLowerInvariant()
    };

    // ---- what it keeps (BR-6, BR-7) ----------------------------------------------------------------------

    private View RenderKeeps(RecordTable table)
    {
        var thing = WhatItKeeps.Plain(table.Definition.Name);
        var fields = _model.Keeps();
        var panel = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(18, 16), BackgroundColor = Theme.Panel };
        panel.Add(new Label { Text = $"What each of your {thing} keeps", FontFamily = Theme.Semibold, FontSize = 15, TextColor = Theme.Bright });
        panel.Add(Theme.Text12($"{Plural(fields.Count, "thing", "things")}, the same for all {table.Total}.", Theme.Dim));

        foreach (var field in fields)
        {
            var block = new VerticalStackLayout { Spacing = 3 };
            block.Add(Theme.Text12(Capitalise(field.Name), Theme.Bright, bold: true));
            block.Add(Theme.Text12(field.Kind, Theme.Text));
            if (field.Notes.Count > 0)
            {
                var tags = new HorizontalStackLayout { Spacing = 6 };
                foreach (var note in field.Notes) tags.Add(Theme.Card(Theme.Text11(note, Theme.Dim), Theme.Window, Theme.Line, new Thickness(8, 2)));
                block.Add(tags);
            }

            SemanticProperties.SetDescription(block, field.Sentence);
            panel.Add(block);
        }

        var connections = _model.Connections();
        if (connections.Count > 0)
        {
            panel.Add(Theme.Text12("How this connects", Theme.Bright, bold: true));
            foreach (var line in connections) panel.Add(Theme.Text12(line, Theme.Text));
        }

        panel.Add(Theme.Text11("You don't have to set any of this up. If you want something different — another thing kept, or one of these gone — just say so in Chat.", Theme.Dimmer));
        SemanticProperties.SetDescription(panel, $"What each of your {thing} keeps");
        return new ScrollView { Content = panel };
    }

    // ---- one record (BR-5, BR-7, BR-8, BR-9) -----------------------------------------------------------

    private View RenderRecord()
    {
        var detail = _model.Detail!;
        var thing = WhatItKeeps.Plain(detail.Thing);
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(28, 20) };

        var back = Theme.Action($"← {_model.ThingTitle(detail.Thing)}");
        back.BackgroundColor = Theme.Window;
        back.TextColor = Theme.Violet;
        back.HorizontalOptions = LayoutOptions.Start;
        back.Clicked += (_, _) => _model.CloseRecord();
        SemanticProperties.SetDescription(back, $"Back to {thing}");
        page.Add(back);

        page.Add(new Label { Text = detail.Title, FontFamily = Theme.Semibold, FontSize = 18, TextColor = Theme.Bright });
        page.Add(Theme.Text12($"One of your {thing} · {thing} last changed {_model.LastChangedWords(detail.Thing)}", Theme.Dim));

        var actions = new HorizontalStackLayout { Spacing = 8 };
        var ask = Theme.Action("Ask about this", primary: true);
        ask.Clicked += (_, _) => _model.AskAbout();
        SemanticProperties.SetDescription(ask, "Carry this one into the conversation, as what the next thing you say is about");
        actions.Add(ask);
        if (!_model.Editing)
        {
            var change = Theme.Action("Change something");
            change.Clicked += (_, _) => _model.StartEditing();
            SemanticProperties.SetDescription(change, "Change one of its values");
            actions.Add(change);
            var remove = Theme.Action("Remove this");
            remove.TextColor = Theme.Loss;
            remove.Clicked += async (_, _) => await _model.RemoveAsync();
            SemanticProperties.SetDescription(remove, "Remove this one; you are asked first, and it can be put back for a while");
            actions.Add(remove);
            var erase = Theme.Action("Erase for good");
            erase.TextColor = Theme.Loss;
            erase.Clicked += async (_, _) => await _model.EraseAsync();
            SemanticProperties.SetDescription(erase, "Erase this one for good; you are asked first, and it cannot be put back");
            actions.Add(erase);
        }

        page.Add(actions);
        if (_model.Notice is { } notice) page.Add(Theme.Text11(notice, Theme.Warn));

        // Every field it has, the empty ones as empty (BR-5); or every field as a box to change.
        var fields = new Grid { ColumnDefinitions = [new ColumnDefinition(160), new ColumnDefinition(GridLength.Star)], RowSpacing = 6, ColumnSpacing = 12 };
        var boxes = new Dictionary<string, Entry>(StringComparer.Ordinal);
        for (var i = 0; i < detail.Fields.Count; i++)
        {
            var field = detail.Fields[i];
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            fields.Add(Theme.Text12(Capitalise(field.Name), Theme.Dim), 0, i);

            if (_model.Editing)
            {
                var box = new Entry { Text = field.IsEmpty ? "" : field.Value, Placeholder = field.Kind, PlaceholderColor = Theme.Dimmer, TextColor = Theme.Text, BackgroundColor = Theme.Panel, FontFamily = Theme.Regular, FontSize = Theme.Small };
                SemanticProperties.SetDescription(box, $"{field.Name}, {field.Kind}");
                boxes[field.Name] = box;
                fields.Add(box, 1, i);
            }
            else
            {
                var value = field.IsEmpty
                    ? Theme.Text12("nothing here", Theme.Dimmer)
                    : Theme.Text12(field.Value + (field.NeedsAttention ? "  (waiting to be made sense of)" : ""), field.NeedsAttention ? Theme.Warn : Theme.Text);
                SemanticProperties.SetDescription(value, field.IsEmpty ? $"{field.Name}: nothing here" : $"{field.Name}: {field.Value}");
                fields.Add(value, 1, i);
            }
        }

        page.Add(Theme.Card(fields, Theme.Panel, Theme.Line, new Thickness(16, 12)));

        if (_model.Editing)
        {
            var buttons = new HorizontalStackLayout { Spacing = 8 };
            var keep = Theme.Action("Save the changes", primary: true);
            keep.Clicked += async (_, _) => await _model.SaveEditsAsync(boxes.ToDictionary(static box => box.Key, static box => box.Value.Text ?? "", StringComparer.Ordinal));
            var leave = Theme.Action("Leave it");
            leave.Clicked += (_, _) => _model.StopEditing();
            SemanticProperties.SetDescription(keep, "Save the changes");
            SemanticProperties.SetDescription(leave, "Leave it as it was");
            buttons.Add(keep);
            buttons.Add(leave);
            page.Add(buttons);
        }

        // Related records one step away, under a heading that says what the relation means (BR-7).
        foreach (var related in detail.Related)
        {
            var block = new VerticalStackLayout { Spacing = 6 };
            block.Add(Theme.Text12(related.Heading, Theme.Bright, bold: true));
            block.Add(Theme.Text11(related.Records.Count == 0 ? "none" : Plural(related.Records.Count, "of them", "of them"), Theme.Dim));
            foreach (var (id, title) in related.Records)
            {
                var link = Theme.Action(title);
                link.BackgroundColor = Theme.Panel;
                link.TextColor = Theme.Violet;
                link.HorizontalOptions = LayoutOptions.Start;
                var (otherThing, otherId) = (related.Thing, id);
                link.Clicked += (_, _) => _model.OpenRecord(otherThing, otherId);
                SemanticProperties.SetDescription(link, $"{title}; opens it");
                block.Add(link);
            }

            page.Add(Theme.Card(block, Theme.Panel, Theme.Line, new Thickness(16, 12)));
        }

        return new ScrollView { Content = page };
    }

    private static string Plural(long count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
