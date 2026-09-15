using System.Text;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Context;

/// <summary>
/// The conversation, bounded (AG-6): a sliding window of the recent turns plus a rolling summary
/// of the older ones, regenerated whenever the window slides.
///
/// <b>The summary is made in C#, not by a model.</b> What has to survive from an old turn is a
/// standing instruction - "always keep costs in euros", "never add a field without asking" - and
/// those are recognisable by their words. Keeping them verbatim is exact where a model's summary
/// would be a paraphrase, costs no call, and is deterministic, which is what lets a test say that
/// a constraint stated in turn 3 is still in the context at turn 150.
/// </summary>
public static class ConversationWindow
{
    /// <summary>How many recent turns are carried verbatim.</summary>
    public const int RecentTurns = 6;

    private static readonly string[] StandingWords =
        ["always", "never", "from now on", "don't", "do not", "only ever", "in future", "every time", "remember that"];

    /// <summary>
    /// The context for these turns, oldest first, within the budget: the standing instructions
    /// found in the older turns, then the recent turns verbatim, trimmed from the oldest end
    /// until it fits.
    /// </summary>
    public static string Render(IReadOnlyList<ConversationTurn> turns, int budgetTokens, int recent = RecentTurns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        if (turns.Count == 0) return string.Empty;

        var older = turns.Take(Math.Max(0, turns.Count - recent)).ToList();
        var window = turns.Skip(Math.Max(0, turns.Count - recent)).ToList();

        var standing = Standing(older);

        while (true)
        {
            var text = new StringBuilder();

            if (standing.Count > 0)
            {
                text.Append("earlier in this conversation the person said:\n");
                foreach (var line in standing) text.Append("- ").Append(line).Append('\n');
                text.Append('\n');
            }

            if (older.Count > 0 && window.Count > 0)
            {
                text.Append("(").Append(older.Count).Append(" earlier turns not shown)\n");
            }

            foreach (var turn in window)
            {
                text.Append(turn.Speaker is TurnSpeaker.Person ? "person: " : "assistant: ")
                    .Append(Shorten(turn.Text, 400)).Append('\n');
            }

            var rendered = text.ToString().TrimEnd('\n');
            if (Tokens.Estimate(rendered) <= budgetTokens) return rendered;

            // Over budget: drop the oldest turn of the window, and when the window is one turn,
            // the least recent standing instruction. Something always fits, because the last
            // turn alone is bounded by the message the person just typed.
            if (window.Count > 1)
            {
                older.Add(window[0]);
                window.RemoveAt(0);
                standing = Standing(older);
                continue;
            }

            if (standing.Count > 0)
            {
                standing.RemoveAt(0);
                continue;
            }

            return Shorten(rendered, Math.Max(40, budgetTokens * 3));
        }
    }

    /// <summary>The standing instructions among older turns: what the person said to remember.</summary>
    public static List<string> Standing(IReadOnlyList<ConversationTurn> older) =>
    [
        .. older
            .Where(static turn => turn.Speaker is TurnSpeaker.Person)
            .Select(static turn => turn.Text.Trim())
            .Where(static text => StandingWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(static text => Shorten(text, 200))
            .Distinct(StringComparer.Ordinal)
    ];

    private static string Shorten(string text, int limit)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= limit ? flat : flat[..(limit - 1)] + "…";
    }
}
