namespace TokkDb.LLM.Storage;

public interface IStorage
{
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

    StorageRecord? GetById(string collectionName, Guid id);

    bool Update(StorageRecord record);

    bool Delete(string collectionName, Guid id);

    IReadOnlyCollection<StorageRecord> GetAll(string collectionName);
}
