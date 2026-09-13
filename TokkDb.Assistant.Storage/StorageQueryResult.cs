namespace TokkDb.Assistant.Storage;

/// <summary>
/// How a query reached its records, in the contract's own words.
///
/// SC-7a: these are storage-owned concepts, not the engine's. An implementation maps whatever it
/// has onto them - the engine maps its <c>QueryReport</c> and its access path - and one with no
/// planner at all reports a scan, because a scan is what it performed.
/// </summary>
public enum QueryAccess
{
    /// <summary>
    /// Every record of the collection was read. Not a failure - it is the right answer when
    /// there is nothing to narrow by - but it is the one that scales with the collection rather
    /// than with the answer, so it is named rather than hidden.
    /// </summary>
    Scan = 1,

    /// <summary>An index was descended once per value asked for.</summary>
    IndexSeek,

    /// <summary>An index was descended to a bound and walked from there.</summary>
    RangeWalk,

    /// <summary>The records were found by identity, without a condition being consulted.</summary>
    IdentityLookup,

    /// <summary>
    /// A set of values was encoded, sorted into key order, and matched against the index in one
    /// walk (SC-9). The path an import takes when it asks whether ten thousand rows are already
    /// there.
    /// </summary>
    OrderedPass
}

/// <summary>
/// What a query did, said in stable concepts of the contract's own (SC-7a).
///
/// <b>Why this is in the result and not in a log.</b> A caller that can read the records without
/// being able to say how they were reached cannot tell a query that will still work at ten
/// thousand records from one that will not. The two look identical until they do not.
///
/// <b>Why every implementation has to report honestly.</b> A fake that returned a "deterministic
/// equivalent" of an index seek would make the shared contract suite pass against a claim no code
/// supports, and the suite exists to catch exactly that. So the in-memory storage reports a scan,
/// which is what a dictionary does, and the assertion that a particular query becomes a seek
/// lives in the engine's own tests where there is a planner to make it true.
/// </summary>
/// <param name="Access">How the records were reached.</param>
/// <param name="CollectionName">The collection read.</param>
/// <param name="ColumnName">The column the access path used, or null.</param>
/// <param name="Description">One line, for a trace step or a test failure.</param>
/// <param name="RecordsExamined">How many records the storage looked at.</param>
/// <param name="RecordsReturned">How many it handed back.</param>
/// <param name="RecordsExcludedByType">
/// How many records were <b>not considered</b> because their value in the column being asked
/// about is not of that column's type yet (SC-6c). Zero for almost every query. It is not a
/// detail: a range over a column where three values are still text answers with the numbers and
/// omits the three, and an answer that looks complete while omitting records is the failure this
/// contract works hardest to prevent. See <see cref="StorageRecord.NeedsAttention"/>.
/// </param>
/// <param name="PagesRead">Pages the storage read, where it knows. Zero where there are no pages.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record QueryExecutionInfo(
    QueryAccess Access,
    string CollectionName,
    string? ColumnName,
    string Description,
    int RecordsExamined,
    int RecordsReturned,
    int RecordsExcludedByType,
    long PagesRead,
    TimeSpan Elapsed)
{
    /// <summary>Whether the storage narrowed the read, rather than reading everything and sifting.</summary>
    public bool IsNarrowed => Access is not QueryAccess.Scan;

    /// <summary>Records the storage looked at and did not want.</summary>
    public int RecordsRejected => RecordsExamined - RecordsReturned;

    /// <summary>Whether any record was left out of the answer for not being of the column's type.</summary>
    public bool IsIncomplete => RecordsExcludedByType > 0;

    public override string ToString()
    {
        var line = $"{Description}: {RecordsExamined} examined, {RecordsReturned} returned, " +
                   $"{PagesRead} pages, {Elapsed.TotalMilliseconds:F2} ms";

        return IsIncomplete
            ? line + $", {RecordsExcludedByType} not considered (not of this column's type yet)"
            : line;
    }
}

/// <summary>
/// The records a query returned, what was computed over them, and how they were reached.
///
/// The records are <see cref="StorageRecord"/>s like any other: values of their columns' types,
/// nothing parsed on the way out. D-6 has them rendered by the application rather than shown to
/// a model, so there is no digest here and no truncation - a thousand rows cost what a thousand
/// rows cost, and no tokens at all.
/// </summary>
/// <param name="Records">The page, in the order asked for.</param>
/// <param name="Execution">How it was served. See <see cref="QueryExecutionInfo"/>.</param>
/// <param name="Aggregates">
/// What the query asked to be computed, by <see cref="QueryAggregate.Key"/>, over everything that
/// matched rather than over the page returned. Empty unless the query asked.
/// </param>
/// <param name="NextCursor">
/// Where to continue from, or null when the page was the end of the records. See
/// <see cref="QueryCursor"/>.
/// </param>
public sealed record StorageQueryResult(
    string CollectionName,
    IReadOnlyList<StorageRecord> Records,
    QueryExecutionInfo Execution,
    IReadOnlyDictionary<string, object?> Aggregates,
    QueryCursor? NextCursor = null)
{
    public StorageQueryResult(
        string collectionName,
        IReadOnlyList<StorageRecord> records,
        QueryExecutionInfo execution)
        : this(collectionName, records, execution, EmptyAggregates)
    {
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyAggregates =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Whether there is another page. See <see cref="NextCursor"/>.</summary>
    public bool HasMore => NextCursor is not null;
}

/// <summary>
/// One value that was looked for and the record that holds it.
/// </summary>
public sealed record ValueMatch(object Value, Ulid RecordId);

/// <summary>
/// The answer to "which of these values are already here", and how it was reached.
///
/// SC-9. The shape matters as much as the answer: a caller that asks about ten thousand values
/// one at a time pays ten thousand descents against an engine with no page cache, and an API
/// that takes a set and loops inside itself hides that loop rather than removing it. This takes
/// the set, and the implementation is expected to encode it, sort it into key order and walk the
/// index once - which turns random reads into sequential ones whatever the tree does underneath.
/// </summary>
public sealed record ValueSetResult
{
    public ValueSetResult(IReadOnlyList<ValueMatch> found, QueryExecutionInfo execution)
    {
        Found = found;
        Execution = execution;

        // Keyed the way the index that answered compares: text folded, everything else as
        // itself. A caller asking about "r-0001" and a stored "R-0001" are asking about one
        // value, and an answer that said otherwise would contradict the uniqueness rule that
        // made the question worth asking.
        var holders = new Dictionary<object, Ulid>();
        foreach (var match in found)
        {
            holders.TryAdd(Key(match.Value), match.RecordId);
        }

        _holders = holders;
    }

    private readonly IReadOnlyDictionary<object, Ulid> _holders;

    /// <summary>Every value that is there, with a record that holds it.</summary>
    public IReadOnlyList<ValueMatch> Found { get; }

    /// <summary>How the set was checked. <see cref="QueryAccess.OrderedPass"/> or a seek per value (SC-9a).</summary>
    public QueryExecutionInfo Execution { get; }

    /// <summary>Whether any record holds that value.</summary>
    public bool Contains(object value) => _holders.ContainsKey(Key(value));

    /// <summary>The record holding that value, or null.</summary>
    public Ulid? Holder(object value) => _holders.TryGetValue(Key(value), out var id) ? id : null;

    private static object Key(object value) => value is string text ? TextComparison.Fold(text) : value;
}
