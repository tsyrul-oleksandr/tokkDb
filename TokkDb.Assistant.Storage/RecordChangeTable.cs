namespace TokkDb.Assistant.Storage;

/// <summary>
/// The detail panel's before-and-after data for one record change (TR-6, AJ-6): every changed
/// column, old beside new, read from the two versions the change names through
/// <see cref="IStorage.DiffVersions"/> - or, once those versions have been purged, the statement
/// that the values are no longer kept, with the request and the time still shown by the caller.
/// </summary>
public sealed record RecordChangeTable(
    string CollectionName,
    Ulid RecordId,
    IReadOnlyList<ColumnChange> Rows,
    bool ValuesAreKept)
{
    public const string NoLongerKept = "the values of this change are no longer kept";

    /// <summary>What the panel says when there is nothing to tabulate: an insert, or purged versions.</summary>
    public string? Note => ValuesAreKept ? null : NoLongerKept;

    /// <summary>
    /// Builds the table for a change: from the version it replaced to the version it produced.
    /// An insert has no version before it, so its table is every column from nothing; a delete's
    /// is every column to nothing.
    /// </summary>
    public static RecordChangeTable For(IStorage storage, string collectionName, Ulid recordId, Ulid? previousVersionId, Ulid? versionId)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (versionId is null)
        {
            return new RecordChangeTable(collectionName, recordId, [], false);
        }

        try
        {
            if (previousVersionId is null)
            {
                // From nothing: the head of the record as the version reads, column by column.
                var head = storage.HeadVersion(collectionName, recordId);
                if (head is null || !storage.Keeps(collectionName, recordId, versionId.Value))
                {
                    return new RecordChangeTable(collectionName, recordId, [], false);
                }

                var inserted = storage.DiffVersions(collectionName, recordId, versionId.Value, versionId.Value);
                var record = storage.GetById(collectionName, recordId);
                var rows = record is null
                    ? []
                    : record.Fields.OrderBy(static field => field.Key, StringComparer.Ordinal)
                        .Select(field => new ColumnChange(field.Key, null, field.Value)).ToList();
                return new RecordChangeTable(inserted.CollectionName, recordId, rows, true);
            }

            var difference = storage.DiffVersions(collectionName, recordId, previousVersionId.Value, versionId.Value);
            return new RecordChangeTable(difference.CollectionName, recordId, difference.Changes, true);
        }
        catch (VersionNotKeptException)
        {
            return new RecordChangeTable(collectionName, recordId, [], false);
        }
    }
}
