using System.Text.Json;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;
using Shown = TokkDb.Assistant.Agents.Orchestration.Values;

namespace TokkDb.Assistant.App.Diagram;

/// <summary>One step with what the panel needs to show it: the changes it made, and the storage to read their versions from.</summary>
public sealed record StepDetail(ExecutionStep Step, IReadOnlyList<DataChange> Changes, IStorage Storage);

/// <summary>
/// The detail panel (TR-6, TR-6a, EX-4): JSON by default, and a purpose-built view where one
/// helps - before-and-after for a record change, read through DiffVersions from the two versions
/// the change names and saying what became of a field since; tokens and timing for a model call;
/// the query and the row count for a retrieval; what changed for a structural step, as its own
/// journal recorded it. A new view is one line in the registry below.
/// </summary>
public static class DetailViews
{
    private static readonly List<(string Kind, Func<StepDetail, bool> Applies, Func<StepDetail, View> Render)> Registry =
    [
        ("a record change", static detail => detail.Changes.Any(static change => change.IsRecordChange), RecordChanges),
        ("a model call", static detail => detail.Step.Call is not null, ModelCall),
        ("a retrieval", static detail => detail.Step.Name == "looking" && detail.Step.Input is not null, Retrieval),
        ("a structural change", static detail => detail.Changes.Count > 0, Structural)
    ];

    /// <summary>The kinds with a view of their own, for a test or a reader to check the registry is the one place.</summary>
    public static IReadOnlyList<string> Kinds => Registry.Select(static entry => entry.Kind).ToList();

    public static View Render(StepDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var view = Registry.FirstOrDefault(entry => entry.Applies(detail)).Render ?? Json;
        return view(detail);
    }

    // ---- a record change: before beside after, with what became of each field since (TR-6a) ----

    private static View RecordChanges(StepDetail detail)
    {
        var panel = new VerticalStackLayout { Spacing = 8 };

        foreach (var change in detail.Changes.Where(static change => change.IsRecordChange))
        {
            var what = change.Kind switch { ChangeKind.Insert => "kept", ChangeKind.Delete => "removed", _ => "changed" };
            panel.Add(Theme.Text11($"{what} in {change.CollectionName.Replace('_', ' ')} · {change.At:HH:mm:ss} · {Reversible(change.Reversibility)}", Theme.Dim));

            if (change.RecordId is not { } id)
            {
                panel.Add(Theme.Text11(RecordChangeTable.NoLongerKept, Theme.Dimmer));
                continue;
            }

            var table = RecordChangeTable.For(detail.Storage, change.CollectionName, id, change.PreviousVersionId, change.VersionId);
            if (!table.ValuesAreKept)
            {
                // The request and the time are still above; only the values are gone.
                panel.Add(Theme.Text11(table.Note ?? RecordChangeTable.NoLongerKept, Theme.Dimmer));
                continue;
            }

            var grid = new Grid
            {
                ColumnDefinitions = [new Microsoft.Maui.Controls.ColumnDefinition(GridLength.Auto), new Microsoft.Maui.Controls.ColumnDefinition(GridLength.Star), new Microsoft.Maui.Controls.ColumnDefinition(GridLength.Star)],
                ColumnSpacing = 12, RowSpacing = 4
            };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Add(Theme.Text11("", Theme.Dim), 0, 0);
            grid.Add(Theme.Text11("before", Theme.Dim), 1, 0);
            grid.Add(Theme.Text11("after", Theme.Dim), 2, 0);

            var row = 1;
            foreach (var column in table.Rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = new VerticalStackLayout { Spacing = 1 };
                name.Add(Theme.Text12(column.ColumnName.Replace('_', ' '), Theme.Bright, bold: true));
                if (column.Note is { } note) name.Add(Theme.Text11(note, Theme.Warn));
                grid.Add(name, 0, row);
                grid.Add(Theme.Text12(column.Before is null ? "—" : Shown.Show(column.Before), column.Before is null ? Theme.Dimmer : Theme.Text), 1, row);
                grid.Add(Theme.Text12(column.After is null ? "—" : Shown.Show(column.After), column.After is null ? Theme.Dimmer : Theme.Text), 2, row);
                SemanticProperties.SetDescription(name, $"{column.ColumnName}: {Shown.Show(column.Before)} before, {Shown.Show(column.After)} after{(column.Note is { } n ? ", " + n : "")}");
                row++;
            }

            panel.Add(grid);
        }

        return panel;
    }

    // ---- a model call: tokens and timing (TR-3, §6.2a) ----

    private static View ModelCall(StepDetail detail)
    {
        var call = detail.Step.Call!;
        var panel = new VerticalStackLayout { Spacing = 4 };
        panel.Add(Theme.Text12($"{call.Model}", Theme.Bright, bold: true));
        panel.Add(Line("tokens in", call.PromptTokens.ToString()));
        panel.Add(Line("tokens out", call.CompletionTokens.ToString()));
        panel.Add(Line("tokens in all", call.TotalTokens.ToString()));
        panel.Add(Line("round trips", call.RoundTrips.ToString()));
        if (call.Retries > 0) panel.Add(Line("repairs", call.Retries.ToString()));
        panel.Add(Line("peak context", call.PeakContextTokens.ToString()));
        panel.Add(Line("took", $"{call.Duration.TotalSeconds:0.00}s"));
        panel.Add(Line("prompt hash", call.PromptHash.Length > 12 ? call.PromptHash[..12] + "…" : call.PromptHash));
        if (detail.Step.Input is { } input) panel.Add(Theme.Text11("asked: " + Shorten(input, 240), Theme.Dim));
        if (detail.Step.Output is { } output) panel.Add(Theme.Text11("answered: " + Shorten(output, 240), Theme.Text));
        return panel;
    }

    // ---- a retrieval: the query in words, and how many rows (TR-6) ----

    private static View Retrieval(StepDetail detail)
    {
        var panel = new VerticalStackLayout { Spacing = 4 };
        try
        {
            var query = QueryJson.Read(detail.Step.Input!);
            panel.Add(Theme.Text12("What was looked for", Theme.Bright, bold: true));
            foreach (var line in QueryJson.Describe(query).Split('\n', StringSplitOptions.RemoveEmptyEntries)) panel.Add(Theme.Text12(line, Theme.Text));
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException or StorageException)
        {
            panel.Add(Theme.Text11(detail.Step.Input!, Theme.Dim));
        }

        if (detail.Step.Output is { } output) panel.Add(Theme.Text11(output, Theme.Dim));
        if (detail.Step.Took is { } took) panel.Add(Line("took", $"{took.TotalMilliseconds:0} ms"));
        return panel;
    }

    // ---- a structural change: what changed, as the journal recorded it then (TR-6a) ----

    private static View Structural(StepDetail detail)
    {
        var panel = new VerticalStackLayout { Spacing = 6 };
        foreach (var change in detail.Changes)
        {
            panel.Add(Theme.Text12($"{Words(change.Kind)} · {change.CollectionName.Replace('_', ' ')} · {Reversible(change.Reversibility)}", Theme.Bright, bold: true));
            foreach (var field in change.Fields)
            {
                var before = field.Before.IsEmpty ? "—" : field.Before.Preview ?? Shown.Show(field.Before.Value);
                var after = field.After.IsEmpty ? "—" : field.After.Preview ?? Shown.Show(field.After.Value);
                panel.Add(Theme.Text12($"{field.Name.Replace('_', ' ')}: {before} → {after}", Theme.Text));
            }

            if (change.OmittedFields > 0) panel.Add(Theme.Text11($"{change.OmittedFields} more values were too large to keep here", Theme.Dimmer));
        }

        return panel;
    }

    // ---- the fallback: the step as it is ----

    private static View Json(StepDetail detail)
    {
        var step = detail.Step;
        var text = JsonSerializer.Serialize(new
        {
            step.Name, Status = step.Status.ToString(), step.StartedAt, step.EndedAt, Took = step.Took?.TotalMilliseconds, step.Input, step.Output
        }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var label = new Label { Text = text, FontFamily = "Menlo", FontSize = Theme.Tiny, TextColor = Theme.Text, LineBreakMode = LineBreakMode.WordWrap };
        SemanticProperties.SetDescription(label, "The step, as recorded");
        return label;
    }

    private static View Line(string what, string value)
    {
        var line = new HorizontalStackLayout { Spacing = 8 };
        line.Add(Theme.Text11(what, Theme.Dim));
        line.Add(Theme.Text12(value, Theme.Text));
        return line;
    }

    private static string Words(ChangeKind kind) => kind switch
    {
        ChangeKind.CollectionAdded => "started keeping",
        ChangeKind.CollectionRemoved => "stopped keeping",
        ChangeKind.FieldAdded => "a field added",
        ChangeKind.FieldChanged => "a field changed",
        ChangeKind.FieldRemoved => "a field dropped",
        ChangeKind.RelationChanged => "a connection changed",
        _ => kind.ToString()
    };

    private static string Reversible(Reversibility reversibility) => reversibility switch
    {
        Reversibility.Reversible => "can be undone",
        Reversibility.ReversibleWithConditions => "can be undone, as long as nothing else has changed it",
        _ => "cannot be undone"
    };

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
