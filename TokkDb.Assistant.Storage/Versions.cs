namespace TokkDb.Assistant.Storage;

/// <summary>
/// One column of a record, as it was in one version beside what it is in another (SC-12,
/// TR-6): the before-and-after table of a record change is a list of these.
/// </summary>
public sealed record ColumnChange(string ColumnName, object? Before, object? After)
{
    /// <summary>What became of the field since these versions were written (TR-6a, SC-12).</summary>
    public FieldFate Fate { get; init; } = FieldFate.Kept;

    /// <summary>For a field since renamed: what it was called then. <see cref="ColumnName"/> is its name now.</summary>
    public string? WasCalled { get; init; }

    /// <summary>For a field since retyped: the kind of value it kept then; the values are shown as they were.</summary>
    public ColumnType? KeptThen { get; init; }

    /// <summary>The plain note beside the row, or nothing for a field that is as it was. A field can have been renamed and then retyped; the note says both.</summary>
    public string? Note
    {
        get
        {
            var parts = new List<string>(3);
            if (Fate.HasFlag(FieldFate.SinceRemoved)) parts.Add("since removed");
            if (Fate.HasFlag(FieldFate.SinceRenamed)) parts.Add($"was called {WasCalled} then");
            if (Fate.HasFlag(FieldFate.SinceRetyped)) parts.Add(KeptThen is { } kind ? $"kept {Kinds.Words(kind)} then; shown as it was" : "since changed to keep another kind of value; shown as it was");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }
}

/// <summary>
/// What the thing's current shape can no longer carry about a field a version holds (TR-6a): a
/// diff shows the value as it was and says what became of the field, rather than dropping it or
/// quietly presenting it under the current shape. Flags, because a field can have been renamed
/// and then retyped, and both are said.
/// </summary>
[Flags]
public enum FieldFate
{
    Kept = 0,
    SinceRemoved = 1,
    SinceRenamed = 2,
    SinceRetyped = 4
}

/// <summary>The kinds of value, in the words a person uses, for a note that names one.</summary>
public static class Kinds
{
    public static string Words(ColumnType type) => type switch
    {
        ColumnType.Text => "some words",
        ColumnType.Integer => "a whole number",
        ColumnType.Decimal => "an amount",
        ColumnType.Boolean => "yes or no",
        ColumnType.Date => "a day",
        ColumnType.Timestamp => "a moment",
        _ => "a value"
    };
}

/// <summary>
/// What <see cref="IStorage.DiffVersions"/> found between two versions of one record: every
/// column whose value differs, old beside new, and whether either version is the record's
/// deletion - in which case every column of the other side is a change to or from nothing.
/// </summary>
public sealed record VersionDifference(
    string CollectionName,
    Ulid RecordId,
    Ulid From,
    Ulid To,
    IReadOnlyList<ColumnChange> Changes,
    bool FromIsDeleted,
    bool ToIsDeleted);

/// <summary>
/// The version a caller named is not kept: it was never written, or a purge has removed it
/// (D-17, AG-11e). An undo that needs it is <c>NotReversible</c>, and a before-and-after table
/// says the values are no longer kept.
/// </summary>
public sealed class VersionNotKeptException : StorageException
{
    public VersionNotKeptException(string collectionName, Ulid recordId, Ulid versionId)
        : base($"Version {versionId} of record {recordId} in '{collectionName}' is not kept: it was never written, or it has been purged.")
    {
        CollectionName = collectionName;
        RecordId = recordId;
        VersionId = versionId;
    }

    public string CollectionName { get; }
    public Ulid RecordId { get; }
    public Ulid VersionId { get; }
}

/// <summary>
/// The version is kept but is not a state a record can be restored to: a deletion. Restoring a
/// deleted record means restoring the version before its deletion, which is what a delete's
/// change names as the version it replaced.
/// </summary>
public sealed class VersionNotRestorableException : StorageException
{
    public VersionNotRestorableException(string collectionName, Ulid recordId, Ulid versionId)
        : base($"Version {versionId} of record {recordId} in '{collectionName}' is its deletion and cannot be restored to.")
    {
        CollectionName = collectionName;
        RecordId = recordId;
        VersionId = versionId;
    }

    public string CollectionName { get; }
    public Ulid RecordId { get; }
    public Ulid VersionId { get; }
}
