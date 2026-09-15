using System.Globalization;
using System.Text.RegularExpressions;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// A follow-up about results already shown, in three layers, cheapest first (QR-3, QR-3b, S-4).
/// One: C# answers from the handle's metadata and the shown records' identities, with no model
/// call, and says it is about what was shown. Two: the model refines the previous query and C#
/// runs it against current data. Three: a bounded digest, as material for phrasing only. In no
/// layer do the rows re-enter the model's context.
/// </summary>
internal static partial class FollowUpFlow
{
    public static async Task<TurnOutcome> HandleAsync(TurnContext ctx, QueryResultHandle handle, string question)
    {
        if (TryLayerOne(ctx, handle, question, out var answer))
        {
            ctx.Tracer.Note("answering from what was shown", input: question, output: answer);
            return ctx.Complete(answer, payload: handle.Write());
        }

        if (Intents.Narrows(question))
        {
            var content = "previous query:\n" + QueryJson.Describe(handle.Query) + $"\ntoday is {ctx.Options.Clock():yyyy-MM-dd}\nfollow-up: {question}";
            var refined = await ctx.RunAsync(Catalogue.Refine, content, EgressClass.SchemaDigest, Answers.Query(ctx.Storage.GetCollectionDefinitions()), summary: question).ConfigureAwait(false);
            var query = RetrievalFlow.Build(ctx, refined.Value);
            return RetrievalFlow.Run(ctx, query, refined.Value.Total, "Against what is stored now:");
        }

        var digest = Digest(ctx, handle);
        var phrased = await ctx.RunAsync(Catalogue.Digest, digest + "\nquestion: " + question, EgressClass.BoundedSample, Answers.Text("answer"), summary: question, withDigest: false).ConfigureAwait(false);
        return ctx.Complete(phrased.Value + " (From a summary of what you were shown, not a fresh look.)", payload: handle.Write());
    }

    [GeneratedRegex(@"\b(how many|count)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountPattern();

    [GeneratedRegex(@"\b(most expensive|priciest|dearest|costliest|highest|largest|biggest|most)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MaxPattern();

    [GeneratedRegex(@"\b(cheapest|least expensive|lowest|smallest|least)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinPattern();

    [GeneratedRegex(@"\b(earliest|first|oldest)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EarliestPattern();

    [GeneratedRegex(@"\b(latest|last|newest|most recent)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LatestPattern();

    [GeneratedRegex(@"\b(the|number) (first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|\d+)( one)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalPattern();

    [GeneratedRegex(@"\b(range|between|from .* to|total|sum|altogether|in all)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RangePattern();

    /// <summary>Layer one: the questions C# can recognise, answered from the handle and the shown records.</summary>
    public static bool TryLayerOne(TurnContext ctx, QueryResultHandle handle, string question, out string answer)
    {
        answer = "";
        var definition = ctx.Storage.GetCollectionDefinition(handle.Thing);
        if (definition is null) return false;

        var thing = Replies.Plain(handle.Thing);

        if (handle.Shown.Count == 0)
        {
            answer = $"Nothing was shown last time - no {thing} matched - so there is nothing to compare. Ask again with different words to look afresh.";
            return true;
        }

        if (CountPattern().IsMatch(question))
        {
            answer = handle.Count == 1 ? $"One: that was all that was shown." : $"{handle.Count} of {thing}: that is how many were shown.";
            return true;
        }

        if (RangePattern().IsMatch(question) && !MaxPattern().IsMatch(question) && !MinPattern().IsMatch(question))
        {
            if (handle.Total is { } total && handle.TotalField is { } field)
            {
                answer = $"{total} in all, over the {handle.Count} {thing} you were shown ({Replies.Plain(field)} added up).";
                return true;
            }

            if (handle.Ranges.Count > 0)
            {
                answer = $"Of the {handle.Count} {thing} you were shown: " + string.Join("; ", handle.Ranges.Select(range => $"{Replies.Plain(range.Key)} runs from {range.Value}")) + ".";
                return true;
            }
        }

        var records = handle.Shown
            .Select(shown => (Shown: shown, Record: ctx.Storage.GetById(handle.Thing, shown.Id)))
            .ToList();

        if (OrdinalPattern().Match(question) is { Success: true } ordinal && !MaxPattern().IsMatch(question) && !MinPattern().IsMatch(question))
        {
            var index = Ordinal(ordinal.Groups[2].Value);
            if (index >= 1 && index <= records.Count)
            {
                var (shown, record) = records[index - 1];
                answer = record is null
                    ? $"The {ordinal.Groups[2].Value} one, {shown.Title}, has since been removed."
                    : $"The {ordinal.Groups[2].Value} one is {shown.Title}: {Describe(definition, record)}.";
                return true;
            }
        }

        var money = definition.Columns.FirstOrDefault(column => Names.Same(column.Name, handle.TotalField ?? ""))
                    ?? definition.Columns.FirstOrDefault(static column => column.Type is ColumnType.Decimal)
                    ?? definition.Columns.FirstOrDefault(static column => column.Type is ColumnType.Integer);
        var date = definition.Columns.FirstOrDefault(static column => column.Type is ColumnType.Date or ColumnType.Timestamp);

        var wantMax = MaxPattern().IsMatch(question);
        var wantMin = MinPattern().IsMatch(question);
        var wantEarliest = EarliestPattern().IsMatch(question);
        var wantLatest = LatestPattern().IsMatch(question);

        if ((wantMax || wantMin) && money is not null)
        {
            return Extreme(records, money, wantMax ? "most" : "least", $"the {(wantMax ? "highest" : "lowest")} {Replies.Plain(money.Name)}", thing, handle.Count, out answer);
        }

        if ((wantEarliest || wantLatest) && date is not null)
        {
            return Extreme(records, date, wantLatest ? "most" : "least", $"the {(wantLatest ? "latest" : "earliest")} {Replies.Plain(date.Name)}", thing, handle.Count, out answer);
        }

        return false;
    }

    private static bool Extreme(List<(ShownRecord Shown, StorageRecord? Record)> records, ColumnDefinition column, string direction, string what, string thing, int count, out string answer)
    {
        var present = records.Where(entry => entry.Record?[column.Name] is not null).ToList();
        if (present.Count == 0)
        {
            answer = $"None of the {thing} you were shown has {Replies.Plain(column.Name)} filled in.";
            return true;
        }

        var ordered = present.OrderBy(entry => entry.Record![column.Name], Comparer<object?>.Create(Compare)).ToList();
        var (shown, record) = direction == "most" ? ordered[^1] : ordered[0];
        var missing = records.Count - present.Count;

        answer = $"Of the {count} {thing} you were shown, {what} is {shown.Title} at {Values.Show(record![column.Name])}."
                 + (missing > 0 ? $" ({missing} of them could not be checked: removed or without a value.)" : "")
                 + (count > records.Count ? $" Only the {records.Count} shown were compared." : "");
        return true;
    }

    private static int Compare(object? left, object? right) => (left, right) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (IComparable l, _) when left.GetType() == right!.GetType() => l.CompareTo(right),
        (decimal l, long r) => l.CompareTo(r),
        (long l, decimal r) => ((decimal)l).CompareTo(r),
        _ => string.CompareOrdinal(left.ToString(), right.ToString())
    };

    private static int Ordinal(string word) => word.ToLowerInvariant() switch
    {
        "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "fifth" => 5,
        "sixth" => 6, "seventh" => 7, "eighth" => 8, "ninth" => 9, "tenth" => 10,
        var digits => int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0
    };

    private static string Describe(CollectionDefinition definition, StorageRecord record) =>
        string.Join(", ", definition.Columns
            .Where(column => record[column.Name] is not null && !Names.Same(column.Name, Fingerprints.ColumnName))
            .Take(6)
            .Select(column => $"{Replies.Plain(column.Name)} {Values.Show(record[column.Name])}"));

    /// <summary>Layer three's material: counts, fields, ranges and a few examples, never the rows (D-6).</summary>
    public static string Digest(TurnContext ctx, QueryResultHandle handle)
    {
        var lines = new List<string>
        {
            $"records shown: {handle.Shown.Count} of {handle.Count} {Replies.Plain(handle.Thing)}",
            "fields: " + string.Join(", ", handle.Fields.Select(Replies.Plain))
        };

        foreach (var range in handle.Ranges) lines.Add($"{Replies.Plain(range.Key)}: from {range.Value}");
        if (handle.Total is { } total) lines.Add($"total of {Replies.Plain(handle.TotalField ?? "")}: {total}");

        lines.Add("examples: " + string.Join("; ", handle.Shown.Take(3).Select(static shown => shown.Title)));
        return string.Join("\n", lines);
    }
}
