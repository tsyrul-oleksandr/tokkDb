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

        // SC-6c: a value that is not of the column's type yet answers nothing asked of that
        // column. This is not a choice - it is what the engine does, and for a reason worth
        // knowing: every encoded key carries its type in its first byte, so a range walk over the
        // numbers never enters the run of text and the record is simply not reached. Matching it
        // here would make the two implementations disagree. What is not allowed is for the
        // omission to be silent, which is why the count comes back in the execution info.
        if (ColumnTypes.TryRecordedType(raw, out var recorded) && recorded != condition.Column.Type)
        {
            return false;
        }

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

    /// <summary>
    /// Whether a record falls after the page a cursor marks, in the order that cursor was issued
    /// for. The comparison is on the pair of sort values and identity, which is what makes a
    /// boundary among equal sort values exact rather than approximately right - see
    /// <see cref="QueryCursor"/>.
    /// </summary>
    public static bool After(
        StorageRecord record,
        IReadOnlyList<(ColumnDefinition Column, bool Descending)> order,
        QueryCursor cursor)
    {
        for (var i = 0; i < order.Count && i < cursor.SortValues.Count; i++)
        {
            var (column, descending) = order[i];

            var held = TextComparison.AsCompared(column.Type, record[column.Name]);
            var mark = TextComparison.AsCompared(column.Type, cursor.SortValues[i]);

            var comparison = SortOrder.Instance.Compare(held, mark);
            if (descending) comparison = -comparison;

            if (comparison != 0) return comparison > 0;
        }

        // Equal on every sort column, so the identity decides - ascending whichever way the sort
        // ran, because that is the tie-break Ordered applies and the two have to agree.
        return record.Id.CompareTo(cursor.RecordId) > 0;
    }

    /// <summary>Where a page ended, as the marker for the next one.</summary>
    public static QueryCursor CursorFor(
        StorageRecord record,
        IReadOnlyList<(ColumnDefinition Column, bool Descending)> order) =>
        new([.. order.Select(entry => record[entry.Column.Name])], record.Id);

    /// <summary>
    /// The record with only the columns asked for. An empty projection is every column, which is
    /// what a caller that did not ask meant.
    ///
    /// A column left out is left out rather than emptied, so a projected record reads exactly as
    /// a record that never had those values - which is the distinction StorageRecord keeps
    /// between absent and nothing, applied to a read rather than a write.
    /// </summary>
    public static StorageRecord Project(StorageRecord record, IReadOnlyList<ColumnDefinition> select)
    {
        if (select.Count == 0) return record;

        var fields = new Dictionary<string, object?>(StorageNames.Comparer);

        foreach (var column in select)
        {
            if (record.Has(column.Name)) fields[column.Name] = record[column.Name];
        }

        var attention = record.NeedsAttention.Count == 0
            ? null
            : record.NeedsAttention.Where(name => select.Any(column => StorageNames.Same(column.Name, name))).ToArray();

        return new StorageRecord(record.Id, record.CollectionName, fields, attention);
    }

    /// <summary>
    /// What the query asked to be computed, over everything that matched rather than over the
    /// page that came back. D-16: the arithmetic is done here and never by a model.
    ///
    /// <b>A total of no values is nothing, not zero.</b> Zero is a claim - "you spent nothing" -
    /// and a collection with no matching records supports no such claim. A count of no records is
    /// a real zero, and is the only one of these that returns one.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Aggregate(
        IReadOnlyList<StorageRecord> matched,
        IReadOnlyList<(QueryAggregate Aggregate, ColumnDefinition? Column)> aggregates)
    {
        var computed = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (aggregate, column) in aggregates)
        {
            if (aggregate.Function is AggregateFunction.Count)
            {
                computed[aggregate.Key] = (long)matched.Count;
                continue;
            }

            var values = matched
                .Select(record => record[column!.Name])
                .Where(static value => value is not null)
                .ToList();

            computed[aggregate.Key] = values.Count == 0
                ? null
                : aggregate.Function switch
                {
                    AggregateFunction.Sum => Total(column!.Type, values),
                    AggregateFunction.Average => Mean(column!.Type, values),
                    AggregateFunction.Minimum => Extreme(column!.Type, values, smallest: true),
                    AggregateFunction.Maximum => Extreme(column!.Type, values, smallest: false),
                    _ => null
                };
        }

        return computed;
    }

    private static object Total(ColumnType type, IReadOnlyList<object?> values) =>
        type is ColumnType.Integer
            ? values.Sum(static value => (long)value!)
            : values.Sum(static value => (decimal)value!);

    /// <summary>
    /// The mean, exact rather than binary floating point. A mean of whole numbers is not a whole
    /// number, so it comes back as a decimal whatever the column keeps - and as a decimal rather
    /// than a double because a total of money divided by a count has to still be money.
    /// </summary>
    private static object Mean(ColumnType type, IReadOnlyList<object?> values)
    {
        var total = type is ColumnType.Integer
            ? values.Sum(static value => (long)value!)
            : values.Sum(static value => (decimal)value!);

        return (decimal)total / values.Count;
    }

    private static object? Extreme(ColumnType type, IReadOnlyList<object?> values, bool smallest)
    {
        object? best = null;
        object? bestCompared = null;

        foreach (var value in values)
        {
            var compared = TextComparison.AsCompared(type, value);

            if (best is null)
            {
                best = value;
                bestCompared = compared;
                continue;
            }

            var comparison = SortOrder.Instance.Compare(compared, bestCompared);

            if (smallest ? comparison < 0 : comparison > 0)
            {
                best = value;
                bestCompared = compared;
            }
        }

        return best;
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
            // Two values of different types, which happens in a column something has been
            // retyped out from under (SC-6b). The engine's keys sort into runs grouped by type
            // tag and its own encoder records that comparing across tags is defined and
            // meaningless; this is the same answer - an order, so that a sort terminates, and no
            // claim that it means anything.
            _ when left.GetType() != right.GetType() => Tag(left).CompareTo(Tag(right)),
            (IComparable a, _) => a.CompareTo(right),
            _ => 0
        };

        private static int Tag(object value) =>
            ColumnTypes.TryRecordedType(value, out var type) ? (int)type : int.MaxValue;
    }
}
