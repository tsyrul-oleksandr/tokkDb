namespace TokkDb.Assistant.Storage;

/// <summary>
/// Whether a record satisfies a condition, and how records are ordered.
///
/// Shared, because an implementation that matched differently from the other would answer the
/// same question two ways and only one of them would be the contract. The engine answers most
/// conditions from an index and re-checks the rest against the page, so this is what the
/// in-memory storage uses and what the engine-backed one has to agree with.
/// </summary>
internal static class QueryMatching
{
    public static bool Matches(StorageRecord record, ValidatedCondition condition)
    {
        var raw = record[condition.Column.Name];
        var present = record.Has(condition.Column.Name) && raw is not null;

        if (condition.Operator is QueryOperator.IsNothing) return !present;
        if (condition.Operator is QueryOperator.IsSomething) return present;

        // Everything else asks something of a value, and a column with no value has none to
        // answer with. A record that was never given a city is not a record whose city is other
        // than Prague.
        if (!present) return false;

        // Text is compared in the form the engine's index keys are in: see TextComparison. The
        // stored value is untouched; this is only what the comparison sees.
        var type = condition.Column.Type;
        var held = TextComparison.AsCompared(type, raw);
        var wanted = condition.Values.Count == 0 ? null : TextComparison.AsCompared(type, condition.Values[0]);

        return condition.Operator switch
        {
            QueryOperator.Equals => Same(held, wanted),
            QueryOperator.NotEquals => !Same(held, wanted),
            QueryOperator.In => condition.Values.Any(value => Same(held, TextComparison.AsCompared(type, value))),

            QueryOperator.LessThan => Compare(held, wanted) < 0,
            QueryOperator.LessOrEqual => Compare(held, wanted) <= 0,
            QueryOperator.GreaterThan => Compare(held, wanted) > 0,
            QueryOperator.GreaterOrEqual => Compare(held, wanted) >= 0,

            QueryOperator.StartsWith => ((string)held!).StartsWith((string)wanted!, StringComparison.Ordinal),
            QueryOperator.EndsWith => ((string)held!).EndsWith((string)wanted!, StringComparison.Ordinal),
            QueryOperator.Contains => ((string)held!).Contains((string)wanted!, StringComparison.Ordinal),

            _ => false
        };
    }

    /// <summary>
    /// Orders records by the columns a query asked for, then by identity, so that the order is
    /// total and repeatable: two records equal on every sort column would otherwise come back in
    /// whatever order the storage happened to hold them, which is the promise GetAll refuses to
    /// make and which a query that was asked for an order has to make.
    /// </summary>
    public static IEnumerable<StorageRecord> Ordered(
        IEnumerable<StorageRecord> records,
        IReadOnlyList<(ColumnDefinition Column, bool Descending)> order)
    {
        if (order.Count == 0) return records;

        IOrderedEnumerable<StorageRecord>? ordered = null;

        foreach (var (column, descending) in order)
        {
            // Ordered in the form text is compared in, so that "Zurich" and "aachen" come out
            // where a person looking at a list would expect rather than with every capital
            // letter before every small one.
            var key = (StorageRecord record) => TextComparison.AsCompared(column.Type, record[column.Name]);

            ordered = ordered is null
                ? descending
                    ? records.OrderByDescending(key, SortOrder.Instance)
                    : records.OrderBy(key, SortOrder.Instance)
                : descending
                    ? ordered.ThenByDescending(key, SortOrder.Instance)
                    : ordered.ThenBy(key, SortOrder.Instance);
        }

        // Then by identity, so the order is total: two records equal on every sort column would
        // otherwise come back in whatever order the storage happened to hold them, which is the
        // promise GetAll refuses to make and which a query asked for an order has to make.
        return ordered!.ThenBy(static record => record.Id);
    }

    private static bool Same(object? left, object? right) => Equals(left, right);

    private static int Compare(object? left, object? right) => SortOrder.Instance.Compare(left, right);

    /// <summary>
    /// One ordering for every column type. Text is compared ordinally, which is the rule names
    /// are compared by and the rule the engine's index keys are built on - a comparison that
    /// moved with the machine's culture would put a query's answer and an index's contents in
    /// different orders.
    ///
    /// A column with no value sorts before every value, so that a column half of whose records
    /// are empty has an order at all.
    /// </summary>
    private sealed class SortOrder : IComparer<object?>
    {
        public static readonly SortOrder Instance = new();

        public int Compare(object? left, object? right) => (left, right) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (string a, string b) => string.CompareOrdinal(a, b),
            (IComparable a, _) => a.CompareTo(right),
            _ => 0
        };
    }
}
