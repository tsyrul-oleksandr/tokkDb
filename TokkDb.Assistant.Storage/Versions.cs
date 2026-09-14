namespace TokkDb.Assistant.Storage;

/// <summary>
/// One column of a record, as it was in one version beside what it is in another (SC-12,
/// TR-6): the before-and-after table of a record change is a list of these.
/// </summary>
public sealed record ColumnChange(string ColumnName, object? Before, object? After);

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
