using System.Text.RegularExpressions;
using TokkDb.Assistant.Agents.Browsing;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>What the person could say next: the options, whether the model wrote them, and the request that asked.</summary>
public sealed record SuggestionSet(IReadOnlyList<string> Options, bool FromModel, Ulid RequestId)
{
    public const int Fewest = 3;
    public const int Most = 7;
}

/// <summary>A few titles the model may name: what was last shown, or what was lately kept when nothing was shown. Titles, never rows.</summary>
public sealed record NamedTitles(string Label, string Thing, IReadOnlyList<string> Titles, int Total);

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
    private const int Longest = 160;

    /// <summary>
    /// A few titles the model may name, at most seven: those last shown or, when nothing was
    /// shown, those of the thing lately changed in this conversation - the one just kept from a
    /// file, say - or else of the first thing stored. Without real names a small model invents
    /// them; with them, its options are about what is there. Titles only, never rows.
    /// </summary>
    public static NamedTitles? TitlesFor(IStorage storage, ITraceRecorder recorder, IReadOnlyList<ConversationTurn> turns, QueryResultHandle? lastShown)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(turns);

        if (lastShown is { Shown.Count: > 0 } handle)
        {
            return new NamedTitles("shown last", handle.Thing, [.. handle.Shown.Take(SuggestionSet.Most).Select(static shown => shown.Title)], handle.Shown.Count);
        }

        var things = storage.GetCollectionDefinitions();
        if (things.Count == 0) return null;

        var lately = turns.Reverse()
            .Where(static turn => turn.RequestId is not null)
            .Select(turn => recorder.Changes(turn.RequestId!.Value).FirstOrDefault()?.CollectionName)
            .FirstOrDefault(static name => name is not null);
        var definition = (lately is null ? null : storage.GetCollectionDefinition(lately)) ?? things.First();
        var page = storage.ExecuteQuery(new StorageQuery(definition.Name, take: SuggestionSet.Most).Computing(new QueryAggregate(AggregateFunction.Count)));
        if (page.Records.Count == 0) return null;

        return new NamedTitles("kept lately", definition.Name, [.. page.Records.Select(record => DisplayValue.For(definition, record))], RetrievalFlow.Count(page));
    }

    /// <summary>
    /// The content the model reads: what is stored, a few titles (so that its options name real
    /// things rather than invented ones), the recent turns, and what was typed. No rows.
    /// </summary>
    public static string Content(IStorage storage, IReadOnlyList<ConversationTurn> turns, NamedTitles? titles, string? draft)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(turns);

        var lines = new List<string>();
        var things = storage.GetCollectionDefinitions();
        lines.Add(things.Count == 0 ? "stored: nothing yet" : "stored: " + string.Join("; ", things.Select(static thing => $"{Replies.Plain(thing.Name)} ({string.Join(", ", thing.Columns.Where(static column => !Names.Same(column.Name, Fingerprints.ColumnName)).Select(static column => Replies.Plain(column.Name)))})")));

        if (titles is { Titles.Count: > 0 })
        {
            var rest = titles.Total > titles.Titles.Count ? $" and {titles.Total - titles.Titles.Count} more" : "";
            lines.Add($"{titles.Label} ({Replies.Plain(titles.Thing)}): {string.Join(", ", titles.Titles)}{rest}");
        }

        var window = ConversationWindow.Render(turns, WindowBudget, recent: 4);
        lines.Add(window.Length == 0 ? "conversation: none yet" : "conversation:\n" + window.TrimEnd());
        lines.Add(string.IsNullOrWhiteSpace(draft) ? "typed so far: nothing" : "typed so far: " + draft.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The options as shown: the model's, cleaned - trimmed, deduplicated, without the words the
    /// first tier forbids, without a placeholder left in, without keeping again a title it was
    /// given, at most seven. When three or more survive they stand alone; when fewer do, C#'s
    /// own follow them, up to the seven, so that there is always something to choose.
    /// </summary>
    public static IReadOnlyList<string> Clean(IReadOnlyList<string> proposed, IReadOnlyList<string> fallback, NamedTitles? titles = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(fallback);

        var kept = new List<string>();
        Add(kept, proposed, titles);
        if (kept.Count < SuggestionSet.Fewest) Add(kept, fallback, null, fromModel: false);
        return kept;
    }

    /// <summary>The words that stand for something in the model's instructions; an option that still carries one was not filled in.</summary>
    private static readonly string[] Unfilled = ["<", ">", "…", "..."];

    private static void Add(List<string> kept, IEnumerable<string> options, NamedTitles? titles, bool fromModel = true)
    {
        foreach (var option in options)
        {
            if (kept.Count == SuggestionSet.Most) return;
            var text = option.Trim().Trim('"').Trim();
            if (text.Length == 0 || text.Length > Longest) continue;
            if (fromModel && Unfilled.Any(mark => text.Contains(mark, StringComparison.Ordinal))) continue;

            // "Keep this: EuroPython, 2025, $150" when EuroPython is already kept: a duplicate with made-up values.
            if (fromModel && titles is not null && text.StartsWith("Keep", StringComparison.OrdinalIgnoreCase)
                && titles.Titles.Any(title => title.Length > 1 && text.Contains(title, StringComparison.OrdinalIgnoreCase))) continue;
            if (ForbiddenWords.Any(word => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase))) continue;
            if (kept.Any(existing => string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(text);
        }
    }

    /// <summary>
    /// What C# can suggest with no model: from what was last shown, the things stored with their
    /// dates and amounts, and what was typed. Grounded by construction, and in the words the
    /// person uses. One option for each thing the assistant does comes first - following up,
    /// correcting, removing, finding, keeping more, taking back - so that the cap of seven leaves
    /// none of them out; the rest follow, and the counts last.
    /// </summary>
    public static IReadOnlyList<string> Starters(IStorage storage, QueryResultHandle? lastShown, bool somethingToTakeBack, string? draft)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var things = storage.GetCollectionDefinitions();
        var leading = new List<string>();
        var more = new List<string>();
        var counts = new List<string>();

        // Following up on, correcting and removing what was last shown.
        var following = false;
        if (lastShown is { Shown.Count: > 0 } handle && storage.GetCollectionDefinition(handle.Thing) is { } shownThing)
        {
            following = true;
            var amount = shownThing.Columns.FirstOrDefault(static column => column.Type is ColumnType.Decimal or ColumnType.Integer);
            var first = handle.Shown[0].Title;
            if (amount is not null)
            {
                leading.Add($"Which of those has the largest {Replies.Plain(amount.Name)}?");
                counts.Add("How many of those are there?");
            }
            else
            {
                leading.Add("How many of those are there?");
            }

            var corrected = amount ?? shownThing.Columns.FirstOrDefault(static column => !Names.Same(column.Name, Fingerprints.ColumnName)) ?? shownThing.Columns[0];
            leading.Add($"{first} should have a different {Replies.Plain(corrected.Name)}: it was actually …");
            leading.Add($"Remove {first}");
        }

        // Finding and adding up, thing by thing; the first thing leads when nothing was shown.
        var index = 0;
        foreach (var thing in things.Take(3))
        {
            var target = index++ == 0 && !following ? leading : more;
            var name = Replies.Plain(thing.Name);
            var date = thing.Columns.FirstOrDefault(static column => column.Type is ColumnType.Date or ColumnType.Timestamp);
            var amount = thing.Columns.FirstOrDefault(static column => column.Type is ColumnType.Decimal or ColumnType.Integer);
            target.Add(date is not null ? $"Show my {name} from this year" : $"Show my {name}");
            if (amount is not null) target.Add($"What do my {name} add up to in {Replies.Plain(amount.Name)}?");
            counts.Add($"How many {name} do I have?");
        }

        // Keeping more, removing something, and taking a change back.
        if (things.Count == 0)
        {
            leading.Add("Keep this: the conference in Lviv on 14 March 2025, 12 000 hryvnia");
            leading.Add("Keep these as trips: Kyiv to Lviv on 3 May 2025, 1 200 hryvnia; Lviv to Odesa on 9 June 2025, 1 850");
            leading.Add("What can you keep for me?");
        }
        else
        {
            leading.Add("Keep these as well: …");
            (following ? more : leading).Add($"Drop something I no longer need from {Replies.Plain(things.First().Name)}");
        }

        if (somethingToTakeBack) leading.Add("Take back the last change");

        var options = leading.Concat(more).Concat(counts).ToList();

        // What was typed comes first: the options that carry it on, then the rest.
        if (!string.IsNullOrWhiteSpace(draft))
        {
            var typed = draft.Trim();
            options = [.. options.Where(option => option.StartsWith(typed, StringComparison.OrdinalIgnoreCase)), .. options.Where(option => !option.StartsWith(typed, StringComparison.OrdinalIgnoreCase))];
        }

        return options.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
