namespace TokkDb.Pages.Versions;

//RB-5 and V-16. What a related restore did: every record it restored, in the order it found
//them, and every record it found already at its version at the moment and left alone.
public sealed class RelatedRestoreResult {
  public DateTimeOffset Moment { get; init; }
  public IReadOnlyList<RestoredRecord> Restored { get; init; } = [];
  public IReadOnlyList<RestoredRecord> AlreadyAtMoment { get; init; } = [];
  public int RecordsVisited => Restored.Count + AlreadyAtMoment.Count;
}

public sealed record RestoredRecord(string CollectionName, Ulid RecordId, Ulid VersionAtMoment, RestoreResult Result);

//V-16: why a related restore was refused before anything was written.
public enum RelatedRestoreRefusal {
  //The record, or a holder it refers to, had no version at the moment, or was deleted then.
  NoVersionAtMoment = 1,
  //No record currently holds the value the relation refers to.
  HolderNotFound = 2,
  //A record holds the value now, but its version at the moment held something else — or had none.
  HolderNotVerified = 3,
  //The set of records to restore grew past the cap (I-6).
  CapExceeded = 4
}

public class RelatedRestoreRefusedException : Exception {
  public RelatedRestoreRefusedException(RelatedRestoreRefusal reason, string collectionName, Ulid recordId,
      string relationName = null, string value = null, int cap = 0, int reached = 0)
    : base(Describe(reason, collectionName, recordId, relationName, value, cap, reached)) {
    Reason = reason;
    CollectionName = collectionName;
    RecordId = recordId;
    RelationName = relationName;
    Value = value;
    Cap = cap;
    Reached = reached;
  }

  public RelatedRestoreRefusal Reason { get; }
  public string CollectionName { get; }
  public Ulid RecordId { get; }
  public string RelationName { get; }
  public string Value { get; }
  public int Cap { get; }
  public int Reached { get; }

  private static string Describe(RelatedRestoreRefusal reason, string collectionName, Ulid recordId, string relationName,
      string value, int cap, int reached) {
    return reason switch {
      RelatedRestoreRefusal.NoVersionAtMoment =>
        $"Record {recordId} of collection {collectionName} has no version at the moment to restore to.",
      RelatedRestoreRefusal.HolderNotFound =>
        $"Relation {relationName} refers to value {value}, which no record of {collectionName} holds now, so the restore of " +
        $"record {recordId} cannot be verified.",
      RelatedRestoreRefusal.HolderNotVerified =>
        $"Relation {relationName} refers to value {value}, and the record of {collectionName} holding it now, {recordId}, did not " +
        "hold it at the moment.",
      _ => $"The related restore reached {reached} records, more than the cap of {cap}."
    };
  }
}
