using System.Globalization;
using System.Text;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Context;

/// <summary>One thing that might be where incoming data belongs, and why it is on the list.</summary>
/// <param name="Definition">The thing.</param>
/// <param name="Score">Between 0 and 1: how much of the incoming shape it accounts for.</param>
/// <param name="MatchedFields">Incoming field names that match one of its fields after normalisation.</param>
/// <param name="MatchedWords">Words of the message that appear in its name or purpose.</param>
public sealed record PlacementCandidate(
    CollectionDefinition Definition,
    double Score,
    IReadOnlyList<string> MatchedFields,
    IReadOnlyList<string> MatchedWords);

/// <summary>
/// The C# pre-filter (§6.1, AG-3c): which things stored are plausible for a message and an
/// incoming shape, ranked, so that the model ranks and corrects a shortlist rather than naming a
/// target freely. Deterministic and cheap: normalised name overlap, a few synonyms a personal
/// storage meets every day, and word overlap between the message and each thing's name and
/// purpose.
/// </summary>
public static class CollectionPrefilter
{
    /// <summary>How many candidates a shortlist holds at most. Five: a small model choosing among few.</summary>
    public const int ShortlistSize = 5;

    /// <summary>
    /// Below this a candidate is not plausible enough to offer, unless it is the best there is:
    /// one field name in common out of four is a coincidence, not a shortlist.
    /// </summary>
    public const double PlausibleScore = 0.3;

    /// <summary>Words that mean the same field in a personal storage, so <c>amount_eur</c> and <c>cost</c> meet.</summary>
    private static readonly string[][] Synonyms =
    [
        ["amount", "cost", "price", "total", "sum", "eur", "euro", "euros", "usd", "money", "spent", "fee", "bill", "paid"],
        ["date", "day", "when", "on", "paidon", "at", "time"],
        ["name", "title", "event", "what"],
        ["city", "place", "where", "location", "venue"],
        ["note", "notes", "comment", "comments", "remark", "remarks", "description"],
        ["person", "who", "with", "contact"]
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "at", "for", "i", "my", "me", "it", "this", "that",
        "these", "those", "is", "are", "was", "were", "be", "with", "from", "by", "as", "want", "save", "keep",
        "store", "put", "please", "here", "some", "all", "one", "did", "do", "how", "what", "which", "much", "many"
    };

    /// <summary>
    /// The shortlist for an incoming shape and a message, best first; empty when nothing is
    /// plausible at all, which for a new kind of thing is the right answer.
    /// </summary>
    public static IReadOnlyList<PlacementCandidate> Shortlist(
        IReadOnlyCollection<CollectionDefinition> definitions,
        IReadOnlyList<string> incomingFields,
        string? message,
        int size = ShortlistSize)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(incomingFields);

        var incoming = incomingFields.Select(Normalise).Where(static name => name.Length > 0).Distinct().ToList();
        var words = Words(message);

        var candidates = new List<PlacementCandidate>();

        foreach (var definition in definitions)
        {
            var columns = definition.Columns
                .Where(column => !Names.Same(column.Name, Fingerprints.ColumnName))
                .Select(static column => column.Name)
                .ToList();

            var matchedFields = new List<string>();
            foreach (var field in incomingFields)
            {
                if (Normalise(field).Length == 0) continue;

                if (columns.Any(column => Same(column, field)))
                {
                    matchedFields.Add(field);
                }
            }

            var nameWords = Words(definition.Name + " " + definition.Purpose);
            var matchedWords = words.Where(word => nameWords.Contains(word) || nameWords.Any(w => Same(w, word))).Distinct().ToList();

            var fieldShare = incoming.Count == 0 ? 0 : (double)matchedFields.Distinct().Count() / incoming.Count;
            var wordShare = words.Count == 0 ? 0 : Math.Min(1.0, matchedWords.Count / 2.0);

            // Fields are the stronger evidence: a spreadsheet whose five columns match five fields
            // belongs here whatever the message said. Words are the tie-breaker and the whole of
            // the evidence when there is prose and no shape yet.
            var score = incoming.Count > 0
                ? 0.8 * fieldShare + 0.2 * wordShare
                : wordShare;

            if (score <= 0) continue;

            candidates.Add(new PlacementCandidate(definition, Math.Round(score, 3), matchedFields.Distinct().ToList(), matchedWords));
        }

        var ranked = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Definition.Name, StringComparer.Ordinal)
            .ToList();

        return [.. ranked.Where((candidate, index) => index == 0 || candidate.Score >= PlausibleScore).Take(size)];
    }

    /// <summary>The shortlist as the mapping operation is shown it: the offered names, with the evidence.</summary>
    public static string Describe(IReadOnlyList<PlacementCandidate> shortlist)
    {
        if (shortlist.Count == 0) return "offered: none - nothing stored looks like this";

        var text = new StringBuilder("offered:\n");
        foreach (var candidate in shortlist)
        {
            text.Append("- ").Append(candidate.Definition.Name);
            if (candidate.MatchedFields.Count > 0)
            {
                text.Append(" (fields in common: ").Append(string.Join(", ", candidate.MatchedFields)).Append(')');
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>A name with its case, spaces, underscores and hyphens gone: <c>Amount EUR</c> and <c>amount_eur</c> are one name.</summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var text = new StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) text.Append(character);
        }

        return text.ToString();
    }

    /// <summary>
    /// Whether two names mean the same field: equal once normalised, one inside the other, or
    /// sharing a synonym as a whole word - <c>paid_on</c> and <c>left_on</c> are both dates,
    /// <c>amount_eur</c> and <c>Cost</c> are both money. Whole words, so that "conference" is not
    /// a date for containing "on".
    /// </summary>
    public static bool Same(string left, string right)
    {
        var l = Normalise(left);
        var r = Normalise(right);

        if (l.Length == 0 || r.Length == 0) return false;
        if (string.Equals(l, r, StringComparison.Ordinal)) return true;
        if (l.Length >= 4 && r.Length >= 4 && (l.Contains(r, StringComparison.Ordinal) || r.Contains(l, StringComparison.Ordinal))) return true;

        var leftWords = NameWords(left);
        var rightWords = NameWords(right);

        foreach (var group in Synonyms)
        {
            if (leftWords.Any(group.Contains) && rightWords.Any(group.Contains)) return true;
        }

        return false;
    }

    /// <summary>The words of a name: <c>amount_eur</c>, <c>paidOn</c> and <c>Paid on</c> each become two.</summary>
    private static List<string> NameWords(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];

        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (char.IsUpper(character) && current.Length > 0 && !char.IsUpper(name[Math.Max(0, name.IndexOf(character) - 1)]))
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) words.Add(current.ToString());

        return words;
    }

    private static List<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return text.ToLower(CultureInfo.InvariantCulture)
            .Split([' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t', '_', '-', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(Singular)
            .Where(word => word.Length > 2 && !StopWords.Contains(word))
            .Distinct()
            .ToList();
    }

    /// <summary>A crude singular, enough for "conferences" to meet "conference".</summary>
    private static string Singular(string word) =>
        word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4 ? word[..^3] + "y"
        : word.EndsWith("es", StringComparison.Ordinal) && word.Length > 4 && (word.EndsWith("ses", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal) || word.EndsWith("ches", StringComparison.Ordinal)) ? word[..^2]
        : word.EndsWith('s') && word.Length > 3 ? word[..^1]
        : word;
}
