namespace TokkDb.Pages.Indexes;

//The tree holds one entry per key. A secondary index over a column with repeated values
//stores the composite key (value, recordId) instead (D-3), so duplicates are made unique by
//the key rather than by a posting list hanging off it.
public class DuplicateIndexKeyException : Exception {
  public DuplicateIndexKeyException(string collectionName, byte[] key)
    : base($"Collection '{collectionName}' already has an index entry for key " +
      $"{Convert.ToHexString(key)}.") {
    CollectionName = collectionName;
    Key = key;
  }

  public string CollectionName { get; }
  public byte[] Key { get; }
}
