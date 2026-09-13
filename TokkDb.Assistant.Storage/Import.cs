namespace TokkDb.Assistant.Storage;

/// <summary>
/// What happened to one incoming row. A small closed set (IN-8).
///
/// Small on purpose: the interface renders these, so every new one is a new thing a person has
/// to understand. Why a row was skipped or rejected is a <see cref="RowReason"/>, which is open
/// and can grow without the set of outcomes growing with it.
/// </summary>
public enum RowOutcome
{
    /// <summary>A record was created from it.</summary>
    Inserted = 1,

    /// <summary>An existing record was changed to match it.</summary>
    Updated,

    /// <summary>Nothing was done with it, and nothing was wrong with it.</summary>
    Skipped,

    /// <summary>Nothing was done with it, and something was wrong with it.</summary>
    Rejected
}

/// <summary>
/// Why a row was skipped or rejected, in a form something can act on.
///
/// IN-8 keeps the outcome and the reason apart, and the reasoning is worth repeating here. An
/// outcome that names its reason - "skipped duplicate" - forces every new reason to become a new
/// outcome. An outcome with no reason - "conflicted" - cannot be acted on, cannot be explained,
/// and cannot be turned into a sentence a person understands. Two axes keep the set the interface
/// renders small and the set the system can explain open.
///
/// <b>No reason is reachable only as free text.</b> Each of these carries its pieces, so the
/// conversation can say "one row matched a conference you already have" and show it.
/// </summary>
public abstract record RowReason
{
    public abstract string Describe();

    public sealed override string ToString() => Describe();
}

/// <summary>The row matched a record already stored, on the key or on the fingerprint.</summary>
public sealed record MatchedStoredRecord(string By, Ulid ExistingRecord, object? Value) : RowReason
{
    public override string Describe() =>
        $"It matches a record already stored, on {By}" +
        (Value is null ? "" : $" ({ColumnTypes.Render(Value)})") + $": {ExistingRecord}.";
}

/// <summary>The row matched an earlier row of the same file.</summary>
public sealed record MatchedEarlierRow(string By, int FirstRow, object? Value) : RowReason
{
    public override string Describe() =>
        $"It matches row {FirstRow} of this file, on {By}" +
        (Value is null ? "" : $" ({ColumnTypes.Render(Value)})") + ".";
}

/// <summary>
/// The row would put a value in a unique column that another record already holds, and the
/// policy does not say what to do about it. <c>Rejected</c>, not a fifth outcome (IN-8).
/// </summary>
public sealed record UniqueValueTaken(string ColumnName, object? Value, Ulid ConflictingRecord) : RowReason
{
    public override string Describe() =>
        $"'{ColumnName}' has to be unique, and {ColumnTypes.Render(Value)} is already held by " +
        $"{ConflictingRecord}.";
}

/// <summary>The row's values do not fit the collection: the same errors a write would give.</summary>
public sealed record RowDoesNotFit(IReadOnlyList<StorageError> Errors) : RowReason
{
    public override string Describe() => string.Join(" ", Errors.Select(static error => error.Describe()));
}

/// <summary>The row never became values at all - the file was malformed at that line (IN-4).</summary>
public sealed record RowUnreadable(string Detail) : RowReason
{
    public override string Describe() => Detail;
}

/// <summary>
/// The row matched something and the policy is <see cref="MergePolicy.Ask"/>, so it is waiting
/// for a person rather than having been decided (IN-8).
/// </summary>
public sealed record AwaitingDecision(string By, Ulid ExistingRecord) : RowReason
{
    public override string Describe() => $"It matches {ExistingRecord} on {By}, and you have not said what to do.";
}

/// <summary>
/// What happened to one row, and why. Persisted with the change record (IN-8a) so that undoing an
/// import deletes what it inserted, restores what it updated and does nothing to what it skipped.
/// </summary>
/// <param name="Row">Which row of the request this was, from zero.</param>
/// <param name="LineNumber">Where it was in the file a person can open, where that is known.</param>
/// <param name="Outcome">One of four.</param>
/// <param name="Reason">Why, for everything that is not a plain insert.</param>
/// <param name="RecordId">The record it became, or matched.</param>
public sealed record RowDisposition(
    int Row,
    int? LineNumber,
    RowOutcome Outcome,
    RowReason? Reason = null,
    Ulid? RecordId = null)
{
    public override string ToString() =>
        $"row {LineNumber?.ToString() ?? Row.ToString()}: {Outcome}" +
        (Reason is null ? "" : $" - {Reason.Describe()}");
}

/// <summary>
/// What to do with a row that is already there (IN-7).
///
/// <b>One default, not two.</b> <see cref="SkipExisting"/>, on the natural key where there is one
/// and on the fingerprint where there is not. An earlier draft of this plan defaulted to adding
/// everything when no key existed, which satisfied one requirement by breaking another: IN-5 says
/// importing the same spreadsheet twice stores no row twice, and a file with no natural key would
/// have stored all of it twice.
/// </summary>
public enum MergePolicy
{
    /// <summary>Rows that are already there are left alone. The default.</summary>
    SkipExisting = 1,

    /// <summary>Every row is stored, whether or not something like it is there. An explicit choice.</summary>
    AddAll,

    /// <summary>
    /// Rows that are already there are brought up to date. <b>Requires a natural key</b>: a
    /// fingerprint covers every value, so a row that changed has a different fingerprint and can
    /// never be matched for update.
    /// </summary>
    UpdateExisting,

    /// <summary>Rows that are already there are left for a person to decide about.</summary>
    Ask
}

/// <summary>One row on its way in: its values, and where it came from.</summary>
/// <param name="Fields">The values, already mapped to the collection's columns.</param>
/// <param name="LineNumber">The line of the file it came from, for IN-4's report.</param>
/// <param name="Unreadable">
/// Set when the row never became values - a malformed line, a date nobody can parse. It is
/// carried into the import rather than dropped before it, because IN-4 requires the bad rows to
/// be reported with their line numbers alongside the good ones being stored.
/// </param>
public sealed record ImportRow(
    IReadOnlyDictionary<string, object?> Fields,
    int? LineNumber = null,
    string? Unreadable = null);

/// <summary>An import, described before it is run.</summary>
/// <param name="CollectionName">Where the rows go.</param>
/// <param name="Rows">The rows, in file order.</param>
/// <param name="Policy">What to do with a row that is already there.</param>
/// <param name="KeyColumn">
/// The natural key, if one was proposed and confirmed (IN-6). Null means identity rests on the
/// fingerprint.
/// </param>
public sealed record ImportRequest(
    string CollectionName,
    IReadOnlyList<ImportRow> Rows,
    MergePolicy Policy = MergePolicy.SkipExisting,
    string? KeyColumn = null);

/// <summary>
/// What an import did, row by row.
///
/// IN-8's counts by outcome with examples, in the shape IN-1a already uses for evidence. The
/// dispositions are the whole of it; the counts are read off them, so the report cannot say
/// "97 stored" while holding 96 dispositions.
/// </summary>
public sealed record ImportReport(
    string CollectionName,
    MergePolicy Policy,
    string? KeyColumn,
    IReadOnlyList<RowDisposition> Rows,
    bool FingerprintVersionChanged = false)
{
    public int Inserted => Count(RowOutcome.Inserted);

    public int Updated => Count(RowOutcome.Updated);

    public int Skipped => Count(RowOutcome.Skipped);

    public int Rejected => Count(RowOutcome.Rejected);

    /// <summary>What the storage now holds because of this import.</summary>
    public IReadOnlyList<Ulid> RecordsWritten =>
        [.. Rows.Where(static row => row.Outcome is RowOutcome.Inserted or RowOutcome.Updated)
            .Select(static row => row.RecordId!.Value)];

    /// <summary>The rows nothing was done with and something was wrong with (IN-4, IN-9).</summary>
    public IReadOnlyList<RowDisposition> Problems =>
        [.. Rows.Where(static row => row.Outcome is RowOutcome.Rejected)];

    /// <summary>How identity was decided: the key where there was one, the fingerprint where not.</summary>
    public string IdentifiedBy => KeyColumn ?? Fingerprints.ColumnName;

    public int Count(RowOutcome outcome) => Rows.Count(row => row.Outcome == outcome);

    /// <summary>
    /// A line for the conversation: "kept 21, skipped 1 that matched on DOI" rather than a count
    /// alone (IN-7).
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (Inserted > 0) parts.Add($"kept {Inserted}");
        if (Updated > 0) parts.Add($"brought {Updated} up to date");
        if (Skipped > 0) parts.Add($"skipped {Skipped} that matched on {IdentifiedBy}");
        if (Rejected > 0) parts.Add($"could not read {Rejected}");

        return parts.Count == 0 ? "there was nothing in it" : string.Join(", ", parts);
    }
}
