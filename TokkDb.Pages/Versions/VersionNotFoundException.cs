namespace TokkDb.Pages.Versions;

//A version the history does not hold: never written, or purged (N-3).
public class VersionNotFoundException : Exception {
  public VersionNotFoundException(string collectionName, Ulid recordId, Ulid versionId)
    : base($"Record {recordId} of collection {collectionName} has no version {versionId}: it was never written, or it is no longer kept.") {
    CollectionName = collectionName;
    RecordId = recordId;
    VersionId = versionId;
  }

  public string CollectionName { get; }
  public Ulid RecordId { get; }
  public Ulid VersionId { get; }
}
