namespace TokkDb.LLM.Storage;

public sealed class FileStorage : IStorage
{
    public void CreateCollection(CollectionDefinition definition) {
        throw new NotImplementedException();
    }
    public bool DeleteCollection(string collectionName) {
        throw new NotImplementedException();
    }
    public CollectionDefinition? GetCollectionDefinition(string collectionName) {
        throw new NotImplementedException();
    }
    public void SetDisplayRule(string collectionName, DisplayRule? displayRule) {
        throw new NotImplementedException();
    }
    public StorageQueryResult ExecuteQuery(StorageQuery query) {
        throw new NotImplementedException();
    }
    public IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions() {
        throw new NotImplementedException();
    }
    public void AddColumn(string collectionName, ColumnDefinition column) {
        throw new NotImplementedException();
    }
    public bool UpdateColumn(string collectionName, string currentColumnName, ColumnDefinition updatedColumn) {
        throw new NotImplementedException();
    }
    public bool RemoveColumn(string collectionName, string columnName) {
        throw new NotImplementedException();
    }
    public void AddRelation(RelationDefinition relation) {
        throw new NotImplementedException();
    }
    public bool RemoveRelation(string relationName) {
        throw new NotImplementedException();
    }
    public RelationDefinition? GetRelation(string relationName) {
        throw new NotImplementedException();
    }
    public IReadOnlyCollection<RelationDefinition> GetRelations() {
        throw new NotImplementedException();
    }
    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields) {
        throw new NotImplementedException();
    }
    public StorageRecord? GetById(string collectionName, Guid id) {
        throw new NotImplementedException();
    }
    public bool Update(StorageRecord record) {
        throw new NotImplementedException();
    }
    public bool Delete(string collectionName, Guid id) {
        throw new NotImplementedException();
    }
    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName) {
        throw new NotImplementedException();
    }
}
