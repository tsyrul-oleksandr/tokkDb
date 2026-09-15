using System.Globalization;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// What the assistant says, templated in C# for the outcomes that recur (AG-1e, UI-2): plain
/// words, no collection, column, index, schema or query. A model phrases only what a template
/// cannot, and by default nothing.
/// </summary>
public static class Replies
{
    /// <summary>A name the person gave that cannot be a thing's name (IN-10): the reason, and what would do.</summary>
    public static string NotAName(string said) =>
        $"'{said}' cannot be a name for a thing: a name starts with a letter, then letters, digits or spaces. Say it again with a name like that, or leave the name to me.";

    public static string NothingToStore(bool hadFile) => hadFile
        ? "There was nothing in it to keep: headings but no rows. Nothing was stored."
        : "There was nothing in that to keep, so nothing was stored.";

    public static string Stored(PlacementProposal proposal, ImportReport report, IReadOnlyList<string> newFields)
    {
        var thing = Plain(proposal.Target);
        var sentence = proposal.IsNew
            ? report.Inserted == 1 ? $"Started keeping {thing} and kept one of them" : $"Started keeping {thing} and kept {report.Inserted} of them"
            : report.Inserted == 1 ? $"Kept one more of {thing}" : $"Kept {report.Inserted} more of {thing}";

        if (newFields.Count == 1) sentence += $", and added a field called {Plain(newFields[0])} to it";
        else if (newFields.Count > 1) sentence += $", and added {newFields.Count} fields to it ({string.Join(", ", newFields.Select(Plain))})";

        if (report.Updated > 0) sentence += $"; brought {report.Updated} up to date";
        if (report.Skipped > 0) sentence += $"; skipped {report.Skipped} already there, matched on {Plain(report.IdentifiedBy == Fingerprints.ColumnName ? "everything in the row" : report.IdentifiedBy)}";

        sentence += ".";

        if (report.Rejected > 0)
        {
            var problems = report.Problems.Take(3)
                .Select(row => row.LineNumber is { } line ? $"line {line}: {row.Reason?.Describe()}" : $"row {row.Row + 1}: {row.Reason?.Describe()}");
            sentence += $" {report.Rejected} could not be kept: {string.Join("; ", problems)}" + (report.Rejected > 3 ? "; and others." : ".");
        }

        return sentence;
    }

    public static string Found(ResultPage page, string? totalField, object? total)
    {
        var thing = Plain(page.Thing);
        var count = page.Total;

        if (totalField is not null && total is not null)
        {
            var noun = count == 1 ? $"one of {thing}" : $"{count} {thing}";
            return $"{Values.Show(total)} in all, over {noun}." + (count > page.Records.Count ? $" Here are the first {page.Records.Count}." : "");
        }

        if (count == 0) return $"Nothing among {thing} matches that.";
        if (count == 1) return $"One of {thing} matches.";
        return count > page.Records.Count
            ? $"{count} {thing} match. Here are the first {page.Records.Count}."
            : $"{count} {thing} match.";
    }

    public static string ShownCount(QueryResultHandle handle) =>
        $"Of the {handle.Count} you were shown, that is all of them: {handle.Count}.";

    public static string Corrected(string thing, string title, string field, object? before, object? after) =>
        $"Changed {Plain(field)} of {title} from {Values.Show(before)} to {Values.Show(after)}.";

    public static string Declined() => "Left as it is. Nothing was changed.";

    public static string Cancelled() => "Stopped. Nothing was changed.";

    public static string Failed(string what) => $"That could not be done: {what}";

    public static string AlreadyDone() => "That was already done, so nothing was changed again.";

    public static string Undone(int records, string thing) =>
        records == 1 ? $"Put one of {Plain(thing)} back as it was." : $"Put {records} of {Plain(thing)} back as they were.";

    /// <summary>A name as a person reads it: underscores to spaces.</summary>
    public static string Plain(string name) => name.Replace('_', ' ');

    public static string Number(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
