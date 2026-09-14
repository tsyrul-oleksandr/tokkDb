namespace TokkDb;

//RP-1 and RP-3. What a purge did — by record, node and operation — and every record it rolled
//back rather than commit a history it could not rebuild, with what was wrong.
public sealed class PurgeReport {
  public string CollectionName { get; init; }
  public DateTimeOffset Before { get; init; }
  public int RecordsExamined { get; init; }
  public int RecordsChanged { get; init; }
  public int NodesRemoved { get; init; }
  public int NodesRerooted { get; init; }
  public int OperationsRemoved { get; init; }
  public IReadOnlyList<PurgeFailure> Failures { get; init; } = [];
  public bool Succeeded => Failures.Count == 0;

  public override string ToString() {
    return $"purge of {CollectionName} before {Before:O}: {RecordsChanged} of {RecordsExamined} records changed, " +
      $"{NodesRemoved} nodes removed, {NodesRerooted} rerooted, {OperationsRemoved} operations removed" +
      (Succeeded ? "" : $", {Failures.Count} records rolled back: " + string.Join("; ", Failures));
  }
}

//A record the purge rolled back, and the check its kept forest failed.
public sealed record PurgeFailure(Ulid RecordId, string Problem);
