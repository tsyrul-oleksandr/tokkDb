namespace TokkDb.LLM.Storage;

public interface IStorage
{
    /// <summary>
    /// Runs <paramref name="work"/> as one unit: everything it writes is applied together or
    /// not at all.
    ///
    /// Without it an import of hundreds of records is hundreds of commits — slow, because each
    /// one pays the durability cost of the whole commit protocol, and worse than slow, because
    /// a failure half way through leaves half the records behind with nothing to say which
    /// half. Inside a batch a failure leaves the storage as it was before the batch began.
    ///
    /// A batch inside a batch joins the outer one, so a method that batches internally can be
    /// called from a caller that is already batching.
    /// </summary>
    void InBatch(Action work);

    void CreateCollection(CollectionDefinition definition);

    bool DeleteCollection(string collectionName);

    CollectionDefinition? GetCollectionDefinition(string collectionName);

    /// <summary>
    /// Sets or clears the collection's display rule. Implementations validate
    /// the rule against the current schema before storing it.
    /// </summary>
    void SetDisplayRule(string collectionName, DisplayRule? displayRule);

    /// <summary>
    /// Validates a bound query against the schema and runs it.
    ///
    /// The query already references definitions rather than names, so what is
    /// checked here is whether they fit: the column belonging to the collection
    /// being filtered, the operator suiting its type, and the operands
    /// converting to it.
    /// </summary>
    /// <exception cref="StorageValidationException">Thrown when the query does not fit the schema.</exception>
    StorageQueryResult ExecuteQuery(StorageQuery query);

    IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions();

    void AddColumn(string collectionName, ColumnDefinition column);

    bool UpdateColumn(string collectionName, string currentColumnName, ColumnDefinition updatedColumn);

    bool RemoveColumn(string collectionName, string columnName);

    void AddRelation(RelationDefinition relation);

    bool RemoveRelation(string relationName);

    RelationDefinition? GetRelation(string relationName);

    IReadOnlyCollection<RelationDefinition> GetRelations();

    StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields);

    StorageRecord? GetById(string collectionName, Ulid id);

    bool Update(StorageRecord record);

    bool Delete(string collectionName, Ulid id);

    IReadOnlyCollection<StorageRecord> GetAll(string collectionName);
}
