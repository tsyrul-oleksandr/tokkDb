namespace TokkDb.Pages.Versions;

//RB-3: why a restore was refused before anything was written. A unique or relation refusal
//comes from the write path itself, typed as any write's would be; these three are the
//restore's own.
public enum RestoreRefusal {
  //The version is not kept: never written, or purged (N-3).
  NoLongerKept = 1,
  //A tombstone is not a state to restore to (N-5).
  Tombstone = 2,
  //The record already reads as this version.
  CurrentHead = 3
}

public class RestoreRefusedException : Exception {
  public RestoreRefusedException(string collectionName, Ulid recordId, Ulid versionId, RestoreRefusal reason)
    : base(Describe(collectionName, recordId, versionId, reason)) {
    CollectionName = collectionName;
    RecordId = recordId;
    VersionId = versionId;
    Reason = reason;
  }

  public string CollectionName { get; }
  public Ulid RecordId { get; }
  public Ulid VersionId { get; }
  public RestoreRefusal Reason { get; }

  private static string Describe(string collectionName, Ulid recordId, Ulid versionId, RestoreRefusal reason) {
    var why = reason switch {
      RestoreRefusal.NoLongerKept => "it is no longer kept",
      RestoreRefusal.Tombstone => "it is a tombstone",
      _ => "it is the current head"
    };
    return $"Version {versionId} of record {recordId} in collection {collectionName} cannot be restored: {why}.";
  }
}
