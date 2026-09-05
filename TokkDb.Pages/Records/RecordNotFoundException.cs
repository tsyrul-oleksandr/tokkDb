namespace TokkDb.Pages;

public class RecordNotFoundException : Exception {
  public Ulid RecordId { get; }
  public string CollectionName { get; }

  public RecordNotFoundException(string collectionName, Ulid recordId, Exception inner = null)
      : base($"No live record {recordId} in collection {collectionName}.", inner) {
    CollectionName = collectionName;
    RecordId = recordId;
  }
}
