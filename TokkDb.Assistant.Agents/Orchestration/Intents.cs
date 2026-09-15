using System.Text.RegularExpressions;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>What a person wants done, as the orchestrator dispatches it (AG-2).</summary>
public enum IntentKind
{
    Store = 1,
    Find,
    FollowUp,
    Correct,
    Remove,
    Restructure,
    Undo,
    Other
}

/// <summary>
/// Intent, decided without a model where the signal is unambiguous (AG-1e, AG-2): an attachment
/// that parsed as a table with no question in the message is storing; "undo" is undo; a question
/// about what was just shown is a follow-up. Everything else goes to the intent operation.
/// </summary>
public static partial class Intents
{
    [GeneratedRegex(@"^\s*(undo|take (that|it) back|put (that|it) back|revert)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UndoPattern();

    [GeneratedRegex(@"\b(how many|how much|count|which (one )?(was|is|were|are) the|the (most|least|cheapest|priciest|biggest|smallest|largest|highest|lowest|earliest|latest|first|last|second|third|fourth|fifth|oldest|newest)\b|what (was|is) the (range|total|sum|average))", RegexOptions.IgnoreCase)]
    private static partial Regex FollowUpPattern();

    [GeneratedRegex(@"\b(of (those|them|these)|among (those|them|these)|those|them|these)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"\b(was actually|is actually|should (be|have been|read)|actually|not \d|wrong|correct(ion)?|change (it|that|the)|fix)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorrectionPattern();

    [GeneratedRegex(@"\b(the (first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last) one|number \d+|#\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PositionalPattern();

    [GeneratedRegex(@"^\s*(remove|delete|get rid of|forget)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemovePattern();

    /// <summary>The intent when no model is needed, or null when it is.</summary>
    public static IntentKind? Deterministic(TurnInput input, bool hasTable, bool hasProse, bool hasHandle, out string reason)
    {
        var text = input.Text?.Trim() ?? string.Empty;

        if (hasTable && !text.Contains('?'))
        {
            reason = "a file that reads as a table, and no question";
            return IntentKind.Store;
        }

        if (hasProse && text.Length == 0)
        {
            reason = "a document and nothing else";
            return IntentKind.Store;
        }

        if (UndoPattern().IsMatch(text))
        {
            reason = "the message asks to take something back";
            return IntentKind.Undo;
        }

        if (hasHandle && PositionalPattern().IsMatch(text) && CorrectionPattern().IsMatch(text))
        {
            reason = "the message points at one of the records shown and says what is wrong";
            return IntentKind.Correct;
        }

        if (hasHandle && FollowUpPattern().IsMatch(text) && (ReferencePattern().IsMatch(text) || text.Split(' ').Length <= 6))
        {
            reason = "a question about what was just shown";
            return IntentKind.FollowUp;
        }

        if (hasHandle && RemovePattern().IsMatch(text) && (ReferencePattern().IsMatch(text) || PositionalPattern().IsMatch(text)))
        {
            reason = "the message asks to remove one of the records shown";
            return IntentKind.Remove;
        }

        reason = "the message does not say by itself";
        return null;
    }

    public static IntentKind FromWord(string word) => word switch
    {
        "store" => IntentKind.Store,
        "find" => IntentKind.Find,
        "correct" => IntentKind.Correct,
        "remove" => IntentKind.Remove,
        "restructure" => IntentKind.Restructure,
        "undo" => IntentKind.Undo,
        _ => IntentKind.Other
    };

    /// <summary>Whether a follow-up narrows what was shown, which is a refined query, rather than asks about it.</summary>
    public static bool Narrows(string text) =>
        Regex.IsMatch(text, @"^\s*(which|only|just|show|list|the ones)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, @"\b(in|from|with|over|under|above|below|before|after|between|without|at|on|by|whose|that|where)\b", RegexOptions.IgnoreCase)
        && !FollowUpPattern().IsMatch(text);

    /// <summary>Whether a removal is asked for with no way back: "for good", "erase", "completely", "permanently", "wipe".</summary>
    public static bool AsksForGood(string message)
    {
        var text = (message ?? "").ToLowerInvariant();
        return text.Contains("for good") || text.Contains("erase") || text.Contains("completely") || text.Contains("permanently") || text.Contains("wipe") || text.Contains("for ever") || text.Contains("forever");
    }

    /// <summary>
    /// The thing the person named for what they are keeping - "keep these as trips", "call them
    /// trips", "these are my trips", "under trips" - or null when they named none. A name the
    /// person gave is not a question for the model: it is the placement, and no mapping call is
    /// made for it (AG-3c).
    /// </summary>
    public static string? NamedThing(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Trim();
        foreach (var lead in new[] { " as my ", " as ", "call them ", "call these ", "call it ", " under ", "these are my ", "these are " })
        {
            var at = text.IndexOf(lead, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var rest = text[(at + lead.Length)..];
            var stop = rest.IndexOfAny([',', '.', '!', '?', ';', ':', '(', ')']);
            if (stop >= 0) rest = rest[..stop];
            var words = rest.Split([' ', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(word => !Connectives.Contains(word.ToLowerInvariant()))
                .Take(3)
                .ToList();
            if (words.Count == 0 || Connectives.Contains(words[0].ToLowerInvariant())) continue;
            return string.Join(' ', words).ToLowerInvariant();
        }

        return null;
    }

    private static readonly HashSet<string> Connectives = ["a", "an", "the", "and", "or", "to", "of", "in", "on", "for", "from", "with", "well", "usual", "before", "please", "then", "so", "too", "also", "new", "is", "are", "was", "not", "rather", "instead", "but"];
}
