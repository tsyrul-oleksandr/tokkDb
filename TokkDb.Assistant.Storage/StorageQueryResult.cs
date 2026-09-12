namespace TokkDb.Assistant.Storage;

/// <summary>How a query reached its records.</summary>
public enum QueryAccessPathKind
{
    /// <summary>
    /// Every record of the collection was read. Not a failure - it is the right answer when
    /// there is nothing to narrow by - but it is the one that scales with the collection rather
    /// than with the answer, so it is named rather than hidden.
    /// </summary>
    FullScan = 1,

    /// <summary>An index was descended once per value asked for.</summary>
    IndexSeek,

    /// <summary>An index was descended to a bound and walked from there.</summary>
    IndexRange,

    /// <summary>The records were found by identity, without a condition being consulted.</summary>
    IdentityLookup
}

/// <summary>
/// What the planner chose, and what it was chosen for.
///
/// SC-7 puts this in the result rather than in a log because a caller that can read the records
/// without being able to say how they were reached cannot tell a query that will still work at
/// ten thousand records from one that will not. The two look identical until they do not.
/// </summary>
public sealed record QueryAccessPath(
    QueryAccessPathKind Kind,
    string CollectionName,
    string? ColumnName,
    string Description)
{
    /// <summary>Whether the storage narrowed the read, rather than reading everything and sifting.</summary>
    public bool IsNarrowed => Kind is not QueryAccessPathKind.FullScan;

    public override string ToString() => Description;
}

/// <summary>
/// What the query cost.
///
/// The measure that matters is the gap between what was looked at and what was kept: a
/// selective question answered by a scan shows up here as a large one, and nowhere else until
/// it is slow enough to notice. TR-3 records these on the trace step, which is what makes the
/// diagram's step for a retrieval say something true about it.
/// </summary>
public sealed record QueryCost(
    int RecordsExamined,
    int RecordsReturned,
    long PagesRead,
    TimeSpan Elapsed)
{
    /// <summary>Records the storage looked at and did not want.</summary>
    public int RecordsRejected => RecordsExamined - RecordsReturned;

    public override string ToString() =>
        $"{RecordsExamined} examined, {RecordsReturned} returned, {PagesRead} pages, {Elapsed.TotalMilliseconds:F2} ms";
}

/// <summary>
/// The records a query returned, and how they were reached.
///
/// The records are <see cref="StorageRecord"/>s like any other: values of their columns' types,
/// nothing parsed on the way out. D-6 has them rendered by the application rather than shown to
/// a model, so there is no digest here and no truncation - a thousand rows cost what a thousand
/// rows cost, and no tokens at all.
/// </summary>
public sealed record StorageQueryResult(
    string CollectionName,
    IReadOnlyList<StorageRecord> Records,
    QueryAccessPath AccessPath,
    QueryCost Cost);
