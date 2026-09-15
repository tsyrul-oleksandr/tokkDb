using TokkDb.Assistant.Agents.Browsing;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>What the person could say next: the options, whether the model wrote them, and the request that asked.</summary>
public sealed record SuggestionSet(IReadOnlyList<string> Options, bool FromModel, Ulid RequestId)
{
    public const int Fewest = 3;
    public const int Most = 7;
}

/// <summary>
/// Suggestions for the composer (UI-9): a read-only operation that looks at what is stored, the
/// recent conversation and what has been typed, and offers three to seven messages the person
/// could send. The model phrases; C# decides what may be shown - the first tier's vocabulary, no
/// duplicates, the count - and writes the options itself when the model cannot, from the things
/// that are stored and what was last shown, so the control never comes back empty.
/// </summary>
public static class Suggestions
{
    /// <summary>The words a suggestion may not contain (UI-2): it is text the person is about to send.</summary>
    public static readonly IReadOnlyList<string> ForbiddenWords = ["collection", "column", "schema", "query", "queries", "index", "database", "table", "record", "records", "field", "fields", "nullable", "constraint"];

    private const int WindowBudget = 700;

    /// <summary>The content the model reads: what is stored, the recent turns, what was typed.</summary>
    public static string Content(IStorage storage, IReadOnlyList<ConversationTurn> turns, string? draft)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(turns);

        var lines = new List<string>();
        var things = storage.GetCollectionDefinitions();
        lines.Add(things.Count == 0 ? "stored: nothing yet" : "stored: " + string.Join("; ", things.Select(static thing => $"{Replies.Plain(thing.Name)} ({string.Join(", ", thing.Columns.Where(static column => !Names.Same(column.Name, Fingerprints.ColumnName)).Select(static column => Replies.Plain(column.Name)))})")));

        var window = ConversationWindow.Render(turns, WindowBudget, recent: 4);
        lines.Add(window.Length == 0 ? "conversation: none yet" : "conversation:\n" + window.TrimEnd());
        lines.Add(string.IsNullOrWhiteSpace(draft) ? "typed so far: nothing" : "typed so far: " + draft.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The options as shown: the model's, cleaned - trimmed, deduplicated, without the words the
    /// first tier forbids, at most seven - topped up from C#'s own when fewer than three remain.
    /// </summary>
    public static IReadOnlyList<string> Clean(IReadOnlyList<string> proposed, IReadOnlyList<string> fallback)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(fallback);

        var kept = new List<string>();
        foreach (var option in proposed.Concat(fallback))
        {
            var text = option.Trim().Trim('"');
            if (text.Length == 0 || text.Length > 160) continue;
            if (ForbiddenWords.Any(word => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{word}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) continue;
            if (kept.Any(existing => string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(text);
            if (kept.Count == SuggestionSet.Most) break;
        }

        return kept;
    }

    /// <summary>
    /// What C# can suggest with no model: from the things stored, their fields, what was last
    /// shown, and what was typed. Grounded by construction, and in the words the person uses.
    /// </summary>
    public static IReadOnlyList<string> Starters(IStorage storage, QueryResultHandle? lastShown, bool somethingToTakeBack, string? draft)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var options = new List<string>();
        var things = storage.GetCollectionDefinitions();

        if (lastShown is { Shown.Count: > 0 } handle && storage.GetCollectionDefinition(handle.Thing) is { } shownThing)
        {
            var amount = shownThing.Columns.FirstOrDefault(static column => column.Type is ColumnType.Decimal or ColumnType.Integer);
            if (amount is not null) options.Add($"Which of those has the largest {Replies.Plain(amount.Name)}?");
            options.Add("How many of those are there?");
            var first = handle.Shown[0].Title;
            var corrected = amount is not null ? Replies.Plain(amount.Name) : Replies.Plain(shownThing.Columns[0].Name);
            options.Add($"{first} should have a different {corrected}: it was actually …");
            options.Add($"Remove {first}");
        }

        foreach (var thing in things.Take(3))
        {
            var name = Replies.Plain(thing.Name);
            var date = thing.Columns.FirstOrDefault(static column => column.Type is ColumnType.Date or ColumnType.Timestamp);
            var amount = thing.Columns.FirstOrDefault(static column => column.Type is ColumnType.Decimal or ColumnType.Integer);
            options.Add(date is not null ? $"Show my {name} from this year" : $"Show my {name}");
            if (amount is not null) options.Add($"What do my {name} add up to in {Replies.Plain(amount.Name)}?");
            options.Add($"How many {name} do I have?");
        }

        if (things.Count == 0)
        {
            options.Add("Keep this: the conference in Lviv on 14 March 2025, 12 000 hryvnia");
            options.Add("What can you keep for me?");
        }
        else
        {
            options.Add($"Keep these as well: …");
            options.Add($"Drop something I no longer need from {Replies.Plain(things.First().Name)}");
        }

        if (somethingToTakeBack) options.Add("Take back the last change");

        // What was typed comes first: the options that carry it on, then the rest.
        if (!string.IsNullOrWhiteSpace(draft))
        {
            var typed = draft.Trim();
            var continuing = options.Where(option => option.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
            var rest = options.Where(option => !option.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
            options = [.. continuing, .. rest];
        }

        return options.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
