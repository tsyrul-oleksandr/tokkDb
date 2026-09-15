using System.Text;
using TokkDb.Assistant.App.Chat;

namespace TokkDb.Assistant.App.Shell;

/// <summary>
/// A way to drive the window from outside it, for the steps that say "run it on Mac Catalyst
/// and report" (5.1, 5.2, 9.7) on a machine where nobody is at the keyboard: a file in the
/// application's data directory lists what to say, the application says it through the same
/// view model a person's typing goes through, screenshots itself after each reply - so no
/// screen-recording permission is involved - writes what came back, and quits.
///
/// Lines: text to send; <c>attach:path</c> attaches a file to the next message; <c>yes</c> and
/// <c>no</c> answer the open question; <c>browse</c> and <c>chat</c> switch surfaces. In the
/// browser: <c>open:thing</c>, <c>sort:field</c>, <c>filter:field|kind|value</c>, <c>find:text</c>,
/// <c>more</c>, <c>restart</c>, <c>csv</c>, <c>keeps</c>, <c>record:part of a title</c>, <c>back</c>,
/// <c>edit:field=value</c>, <c>remove</c>, <c>ask</c>, <c>overview</c>. Nothing here runs unless
/// the file exists.
/// </summary>
public static class SelfTest
{
    public static string ScriptPath => Path.Combine(FileSystem.AppDataDirectory, "selftest.txt");
    public static string LogPath => Path.Combine(FileSystem.AppDataDirectory, "selftest.log");

    public static async Task RunIfAskedAsync(ChatViewModel model, Browse.BrowseViewModel browse, Func<string, Task> screenshot, Action<bool> showSurface)
    {
        if (!File.Exists(ScriptPath)) return;

        var log = new StringBuilder();
        var lines = await File.ReadAllLinesAsync(ScriptPath);
        File.Delete(ScriptPath);

        void Note(string text)
        {
            log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
            File.WriteAllText(LogPath, log.ToString());
        }

        Note($"self-test started: {lines.Length} lines; database {Settings.AppSettings.DatabasePath}");
        await Task.Delay(1500);

        var shot = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            try
            {
                if (line.StartsWith("attach:", StringComparison.Ordinal))
                {
                    var failures = new List<string>();
                    void Collect(string message) => failures.Add(message);
                    model.Failed += Collect;
                    await model.AttachAsync([line["attach:".Length..].Trim()]);
                    model.Failed -= Collect;
                    foreach (var failure in failures) Note("attach failed: " + failure);
                    Note($"attached: {string.Join("; ", model.Attachments.Select(a => a.Name + " - " + string.Join(" | ", a.Understood) + " - " + a.WhatHappensNext))}");
                    await Task.Delay(400);
                    await screenshot(Path.Combine(FileSystem.AppDataDirectory, $"selftest-{++shot:00}.png"));
                    continue;
                }

                if (line is "browse" or "chat")
                {
                    showSurface(line == "chat");
                    await Task.Delay(600);
                    await screenshot(Path.Combine(FileSystem.AppDataDirectory, $"selftest-{++shot:00}.png"));
                    Note($"surface: {line}");
                    continue;
                }

                if (line == "crash")
                {
                    // N-9: the process goes with whatever is on screen - no quit, no flush, no answer.
                    Note("crash: the process is ending now, with the question on screen");
                    Environment.FailFast("self-test: a crash while waiting (N-9)");
                }

                if (line.StartsWith("cancel:", StringComparison.Ordinal))
                {
                    // N-7: the request is cancelled while it runs; the state reaches Cancelled and nothing is half-applied.
                    model.Draft = line["cancel:".Length..].Trim();
                    var sending = model.SendAsync();
                    await Task.Delay(700);
                    model.Cancel();
                    await sending;
                    var cancelled = model.Messages.LastOrDefault(m => !m.FromPerson);
                    var request = cancelled?.RequestId is { } id ? model.Recorder.Request(id) : null;
                    Note($"cancelled: reply \"{cancelled?.Text}\"; state {request?.State}; things: {string.Join(", ", model.Storage.GetCollectionDefinitions().Select(d => d.Name + "=" + model.Storage.GetAll(d.Name).Count))}");
                    await Task.Delay(400);
                    await screenshot(Path.Combine(FileSystem.AppDataDirectory, $"selftest-{++shot:00}.png"));
                    continue;
                }

                if (await BrowseAsync(model, browse, line, Note, showSurface))
                {
                    await Task.Delay(500);
                    await screenshot(Path.Combine(FileSystem.AppDataDirectory, $"selftest-{++shot:00}.png"));
                    continue;
                }

                if (line is "yes" or "no")
                {
                    var open = model.Messages.LastOrDefault(m => m.QuestionOpen);
                    if (open is null) { Note("no open question to answer"); continue; }
                    await model.AnswerAsync(open, line == "yes");
                }
                else
                {
                    model.Draft = line;
                    await model.SendAsync();
                }

                var last = model.Messages.LastOrDefault(m => !m.FromPerson);
                Note($"said: {line}");
                Note($"reply: {last?.Text}" + (last?.QuestionOpen == true ? $" [question: {last.Question?.Title}]" : "") + (last?.Results is { } page ? $" [{page.Records.Count} of {page.Total} shown]" : ""));
                Note($"steps: {string.Join(" -> ", model.Steps.Select(s => s.Name + (s.IsModelCall ? "*" : "")))}");
                await Task.Delay(600);
                await screenshot(Path.Combine(FileSystem.AppDataDirectory, $"selftest-{++shot:00}.png"));
            }
            catch (Exception failure)
            {
                Note($"FAILED at '{line}': {failure.GetType().Name}: {failure.Message}");
            }
        }

        Note("self-test finished");
        await Task.Delay(500);
        Application.Current?.Quit();
    }

    /// <summary>The browser's lines; true when the line was one of them. Each writes what the browser then shows.</summary>
    private static async Task<bool> BrowseAsync(ChatViewModel model, Browse.BrowseViewModel browse, string line, Action<string> note, Action<bool> showSurface)
    {
        var colon = line.IndexOf(':');
        var word = colon < 0 ? line : line[..colon];
        var rest = colon < 0 ? "" : line[(colon + 1)..].Trim();

        switch (word)
        {
            case "overview":
                browse.ShowOverview();
                note("overview: " + string.Join(" | ", browse.Things.Select(thing => $"{thing.Title} ({thing.Count}, changed {thing.When}): {thing.Keeps}")));
                return true;
            case "open":
            {
                // By name, or by the start of one: the script names what the person would click.
                var thing = browse.Things.FirstOrDefault(t => string.Equals(t.Thing, rest, StringComparison.OrdinalIgnoreCase))
                            ?? browse.Things.FirstOrDefault(t => t.Thing.StartsWith(rest, StringComparison.OrdinalIgnoreCase) || t.Title.StartsWith(rest, StringComparison.OrdinalIgnoreCase));
                browse.OpenThing(thing?.Thing ?? rest);
                NoteTable(browse, note);
                return true;
            }
            case "sort":
            {
                // By name, or by the column's number as the table shows them, since the model names fields as it will.
                var field = int.TryParse(rest, out var index) && browse.Table is { } table && index >= 1 && index <= table.Columns.Count ? table.Columns[index - 1].Name : rest;
                browse.SortBy(field);
                NoteTable(browse, note);
                return true;
            }
            case "filter":
            {
                var parts = rest.Split('|');
                browse.AddFilter(new Agents.Browsing.TableFilter(parts[0].Trim(), Enum.Parse<Agents.Browsing.FilterKind>(parts[1].Trim(), ignoreCase: true), parts.Length > 2 ? parts[2].Trim() : null));
                NoteTable(browse, note);
                return true;
            }
            case "find":
                browse.Find(rest);
                NoteTable(browse, note);
                return true;
            case "more":
                browse.More();
                NoteTable(browse, note);
                return true;
            case "restart":
                browse.Restart();
                NoteTable(browse, note);
                return true;
            case "csv":
            {
                var path = browse.SaveCsv();
                note(path is null ? "csv: nothing open" : $"csv: {path}: {string.Join(" / ", File.ReadLines(path).Take(3))}");
                return true;
            }
            case "keeps":
                browse.ToggleKeeps();
                note("keeps: " + string.Join(" | ", browse.Keeps().Select(field => field.Sentence)) + (browse.Connections().Count > 0 ? " || " + string.Join(" | ", browse.Connections()) : ""));
                return true;
            case "record":
            {
                var row = int.TryParse(rest.TrimStart('#'), out var position) && browse.Table is { } rows && position >= 1 && position <= rows.Rows.Count
                    ? rows.Rows[position - 1]
                    : browse.Table?.Rows.FirstOrDefault(row => row.Title.Contains(rest, StringComparison.OrdinalIgnoreCase) || row.Cells.Any(cell => cell.Contains(rest, StringComparison.OrdinalIgnoreCase)));
                if (row is null || browse.Table is null) { note($"record: nothing shown has '{rest}' in its name"); return true; }
                browse.OpenRecord(browse.Table.Definition.Name, row.Id);
                NoteRecord(browse, note);
                return true;
            }
            case "back":
                browse.CloseRecord();
                NoteTable(browse, note);
                return true;
            case "edit":
            {
                var equals = rest.IndexOf('=');
                if (equals < 0 || browse.Detail is null) { note("edit: say field=value with a record open"); return true; }
                browse.StartEditing();
                var texts = browse.Detail.Fields.ToDictionary(field => field.Name, field => field.IsEmpty ? "" : field.Value, StringComparer.Ordinal);
                texts[rest[..equals].Trim()] = rest[(equals + 1)..].Trim();
                var saved = await browse.SaveEditsAsync(texts);
                note($"edit: {(saved ? "saved" : "refused")}; {browse.Notice}");
                NoteRecord(browse, note);
                NoteChat(model, note);
                return true;
            }
            case "remove":
                await browse.RemoveAsync();
                NoteChat(model, note);
                return true;
            case "ask":
                browse.AskAbout();
                await Task.Delay(300);
                note($"ask: draft \"{model.Draft}\"; last shown: {model.Messages.LastOrDefault(m => !m.FromPerson)?.Text}");
                return true;
            case "a11y":
            {
                // UI-7: the accessibility tree as the platform will read it - every interactive
                // element on the visible surface, and whether it has a name a screen reader can say.
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page is null) { note("a11y: no page"); return true; }
                var interactive = page.GetVisualTreeDescendants().OfType<View>()
                    .Where(view => view.IsVisible && (view is Button or Entry or Editor or Picker or Switch or CheckBox || view.GestureRecognizers.Count > 0))
                    .ToList();
                var unnamed = interactive.Where(view => string.IsNullOrWhiteSpace(SemanticProperties.GetDescription(view)) && !(view is Button button && !string.IsNullOrWhiteSpace(button.Text)) && !(view is Entry entry && !string.IsNullOrWhiteSpace(entry.Placeholder)) && !(view is Picker picker && !string.IsNullOrWhiteSpace(picker.Title))).ToList();
                note($"a11y: {interactive.Count} interactive elements on screen, {interactive.Count - unnamed.Count} named; unnamed: {(unnamed.Count == 0 ? "none" : string.Join(", ", unnamed.Take(12).Select(view => view.GetType().Name + (view is Label label ? $" \"{label.Text}\"" : ""))))}");
                note($"a11y: focus order: {string.Join(" > ", page.GetVisualTreeDescendants().OfType<View>().Where(view => view.IsVisible && view is Button or Entry or Editor or Picker).Take(24).Select(view => SemanticProperties.GetDescription(view) ?? (view as Button)?.Text ?? (view as Entry)?.Placeholder ?? view.GetType().Name))}");
                return true;
            }
            default:
                return false;
        }
    }

    private static void NoteTable(Browse.BrowseViewModel browse, Action<string> note)
    {
        if (browse.Table is not { } table) { note("table: none open; " + browse.Notice); return; }
        note($"table: {table.Definition.Name} {table.Rows.Count} of {table.Total} shown, {browse.SortWords}; filters: {(table.Filters.Count == 0 ? "none" : string.Join(", ", table.Filters.Select(f => f.Describe())))}; " +
             $"{(table.LastExecution is { } run ? $"{run.Access}, {run.RecordsExamined} examined, {run.PagesRead} pages" : "not run")}; changed underneath: {browse.ChangedUnderneath}" +
             (browse.Notice is { } notice ? $"; notice: {notice}" : "") + (table.Rows.Count > 0 ? $"; first: {table.Rows[0].Title}" : ""));
    }

    private static void NoteRecord(Browse.BrowseViewModel browse, Action<string> note)
    {
        if (browse.Detail is not { } detail) { note("record: none open; " + browse.Notice); return; }
        note($"record: {detail.Title}: {string.Join("; ", detail.Fields.Select(field => $"{field.Name}={(field.IsEmpty ? "(empty)" : field.Value)}"))}" +
             (detail.Related.Count > 0 ? " || " + string.Join(" | ", detail.Related.Select(r => $"{r.Heading}: {r.Records.Count}")) : ""));
    }

    private static void NoteChat(ChatViewModel model, Action<string> note)
    {
        var last = model.Messages.LastOrDefault(m => !m.FromPerson);
        note($"chat: {last?.Text}" + (last?.QuestionOpen == true ? $" [question: {last.Question?.Title}]" : ""));
        note($"steps: {string.Join(" -> ", model.Steps.Select(s => s.Name + (s.IsModelCall ? "*" : "")))}");
    }
}
