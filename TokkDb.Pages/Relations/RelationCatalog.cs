using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using TokkDb.Transactions;

namespace TokkDb.Pages.Relations;

//The referential constraints of the database, stored as documents in _relations (D-4) and
//read at open with the rest of the catalogue.
public class RelationCatalog {
  private readonly CollectionCatalog _catalog;
  private readonly IndexCatalog _indexes;
  private readonly TransactionManager _transactionManager;
  private DataPageManager _dataPageManager;

  //Source collection to the relations constraining it. The write path asks for this on every
  //record, and for almost every collection the answer is nothing.
  private readonly Dictionary<string, List<RelationDescriptor>> _bySource = new(StringComparer.Ordinal);

  public RelationCatalog(CollectionCatalog catalog, IndexCatalog indexes,
      TransactionManager transactionManager) {
    _catalog = catalog;
    _indexes = indexes;
    _transactionManager = transactionManager;
  }

  public void SetDataPageManager(DataPageManager dataPageManager) {
    _dataPageManager = dataPageManager;
  }

  public IEnumerable<RelationDescriptor> Descriptors => _bySource.Values.SelectMany(relations => relations);

  public void Initialize() {
    _bySource.Clear();
    foreach (var row in _dataPageManager.GetAllRows(SystemCollections.Relations)) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (record.Header.IsLive) {
        Register(RelationDescriptorDocument.Read(record.Document));
      }
    }
  }

  //The relations a write of this collection has to satisfy.
  public IReadOnlyList<RelationDescriptor> From(string collectionName) {
    return _bySource.TryGetValue(collectionName, out var relations) ? relations : [];
  }

  //Creating a relation creates the index its check needs, if the target column has not got
  //one already. That is DC-4's "referential checks need an index on the target" made
  //structural: there is no way to have the constraint without the index that affords it.
  public RelationDescriptor Create(string name, string sourceCollection, string sourceColumn,
      string targetCollection, string targetColumn) {
    _transactionManager.RequireTransaction();
    if (Descriptors.Any(relation => relation.Name == name)) {
      throw new ArgumentException($"Relation '{name}' already exists.", nameof(name));
    }
    RequireColumn(sourceCollection, sourceColumn);
    RequireColumn(targetCollection, targetColumn);
    if (_indexes.Find(targetCollection, targetColumn) is null) {
      _indexes.Create(targetCollection, targetColumn);
    }
    var descriptor = new RelationDescriptor {
      Id = RecordIdentity.Next(),
      Name = name,
      SourceCollection = sourceCollection,
      SourceColumn = sourceColumn,
      TargetCollection = targetCollection,
      TargetColumn = targetColumn
    };
    Register(descriptor);
    var header = RecordHeader.ForNewRecord(descriptor.Id, 1);
    _dataPageManager.WriteRecord(SystemCollections.Relations, header,
      RelationDescriptorDocument.Write(descriptor));
    return descriptor;
  }

  private void RequireColumn(string collectionName, string columnName) {
    var collection = _catalog.Get(collectionName);
    if (collection.Columns.Count > 0 && collection.Columns.All(column => column.Name != columnName)) {
      throw new ArgumentException(
        $"Collection '{collectionName}' has no column '{columnName}'.", nameof(columnName));
    }
  }

  private void Register(RelationDescriptor descriptor) {
    if (!_bySource.TryGetValue(descriptor.SourceCollection, out var relations)) {
      relations = [];
      _bySource[descriptor.SourceCollection] = relations;
    }
    relations.Add(descriptor);
  }
}
