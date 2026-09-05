namespace TokkDb.LLM.Storage;

public sealed record StorageRecord
{
    public StorageRecord(Ulid id, string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        Id = id;
        CollectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        Fields = new Dictionary<string, object?>(fields, StringComparer.Ordinal);
    }

    public Ulid Id { get; }

    public string CollectionName { get; }

    public IReadOnlyDictionary<string, object?> Fields { get; }
}
