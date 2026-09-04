namespace TokkDb.Pages;

//The "_" prefix belongs to the engine. A user collection may not claim it.
public class ReservedCollectionNameException : Exception {
  public string CollectionName { get; }

  public ReservedCollectionNameException(string collectionName, Exception inner = null)
      : base($"The collection name '{collectionName}' is reserved: names beginning with " +
        $"'{SystemCollections.ReservedPrefix}' belong to the engine.", inner) {
    CollectionName = collectionName;
  }
}
