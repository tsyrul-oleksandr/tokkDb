using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using TokkDb.Transactions;
using TokkDb.Values;

namespace TokkDb.Pages.Indexes;

//DC-4 and D-4. The secondary indexes of every collection: their descriptors are documents in
//_indexes, read at open like any other catalogue, and their roots are physical pointers in
//the collection's own catalogue document (D-2).
public class IndexCatalog {
  private readonly PageManager _pageManager;
  private readonly CollectionCatalog _catalog;
  private readonly FreeSpaceManager _freeSpace;
  private readonly TransactionManager _transactionManager;
  private DataPageManager _dataPageManager;

  //Collection name to its indexes. Empty for most collections, which is why the maintenance
  //path can ask for the list on every write without it costing anything.
  private readonly Dictionary<string, List<SecondaryIndex>> _byCollection = new(StringComparer.Ordinal);

  public IndexCatalog(PageManager pageManager, CollectionCatalog catalog, FreeSpaceManager freeSpace,
      TransactionManager transactionManager) {
    _pageManager = pageManager;
    _catalog = catalog;
    _freeSpace = freeSpace;
    _transactionManager = transactionManager;
  }

  //The same knot CollectionCatalog has: the descriptors are records, so reading them needs
  //the manager that reads records, and that manager needs these to maintain them.
  public void SetDataPageManager(DataPageManager dataPageManager) {
    _dataPageManager = dataPageManager;
  }

  public IEnumerable<IndexDescriptor> Descriptors =>
    _byCollection.Values.SelectMany(indexes => indexes).Select(index => index.Descriptor);

  //Read at open, so an index is never rebuilt by scanning the collection it covers.
  public void Initialize() {
    _byCollection.Clear();
    foreach (var row in _dataPageManager.GetAllRows(SystemCollections.Indexes)) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (!record.Header.IsLive) {
        continue;
      }
      Register(IndexDescriptorDocument.Read(record.Document));
    }
  }

  public IReadOnlyList<SecondaryIndex> For(string collectionName) {
    return _byCollection.TryGetValue(collectionName, out var indexes) ? indexes : [];
  }

  public SecondaryIndex Find(string collectionName, string columnName) {
    return For(collectionName).FirstOrDefault(index => index.Descriptor.ColumnName == columnName);
  }

  //Creating an index over a collection that already holds records builds it from them, in
  //the same transaction. That is the one scan an index costs; after it, nothing reads a
  //collection to find out what its index contains.
  public SecondaryIndex Create(string collectionName, string columnName, bool unique = false) {
    _transactionManager.RequireTransaction();
    var collection = _catalog.Get(collectionName);
    if (collection.Columns.Count > 0 && collection.Columns.All(column => column.Name != columnName)) {
      throw new ArgumentException(
        $"Collection '{collectionName}' has no column '{columnName}' to index.", nameof(columnName));
    }
    if (Find(collectionName, columnName) is { } existing) {
      throw new ArgumentException(
        $"Column '{columnName}' of collection '{collectionName}' is already indexed.", nameof(columnName));
    }
    if (collection.IsSystem) {
      throw new ArgumentException(
        $"Collection '{collectionName}' belongs to the catalogue and is not indexed in this pass.",
        nameof(collectionName));
    }
    //Refused here rather than at the first write: an object and an array have no ordering, so
    //there is no key an index over one of them could be sorted by.
    var column = collection.Columns.FirstOrDefault(candidate => candidate.Name == columnName);
    if (column is not null && column.Type is ValueTypeEnum.Object or ValueTypeEnum.Array) {
      throw new ArgumentException(
        $"Column '{columnName}' of collection '{collectionName}' holds {column.Type} values, which have " +
        $"no ordering and cannot be an index key.", nameof(columnName));
    }
    var descriptor = new IndexDescriptor {
      Id = RecordIdentity.Next(),
      CollectionName = collectionName,
      ColumnName = columnName,
      Unique = unique
    };
    var index = Register(descriptor);
    Append(descriptor);
    Build(index);
    return index;
  }

  //DC-4. Removes an index: its pages go back to the collection's retired pool, its root
  //leaves the catalogue document (D-2) and its descriptor leaves _indexes. All of it in one
  //transaction, so a failure part-way leaves the index exactly as it was.
  //
  //The pages are recorded as retired rather than freed outright because that is the pool the
  //trees of this collection take from — the same recycling a merge uses when it empties a
  //node, so a dropped index makes room for the next one instead of growing the file.
  public bool Drop(string collectionName, string columnName) {
    _transactionManager.RequireTransaction();
    if (Find(collectionName, columnName) is not { } index) {
      return false;
    }
    foreach (var node in index.Tree.Nodes()) {
      _freeSpace.RecordIndexPage(collectionName, node.Index, inUse: false);
    }
    _catalog.RemoveSecondaryIndexRoot(collectionName, index.Descriptor.Name);
    if (_dataPageManager.FindLiveRow(SystemCollections.Indexes, index.Descriptor.Id) is { } row) {
      _dataPageManager.RetireRow(SystemCollections.Indexes, row.Address, RecordFlags.Deleted,
        RetentionPolicy.None);
    }
    _byCollection[collectionName].Remove(index);
    return true;
  }

  //Every index of a collection that is going away. Dropping them one at a time from outside
  //would iterate the list being modified.
  public void DropAll(string collectionName) {
    foreach (var columnName in For(collectionName).Select(index => index.Descriptor.ColumnName).ToArray()) {
      Drop(collectionName, columnName);
    }
  }

  private void Build(SecondaryIndex index) {
    var collectionName = index.Descriptor.CollectionName;
    foreach (var row in _dataPageManager.GetAllRows(collectionName)) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (!record.Header.IsLive) {
        continue;
      }
      var value = ReadColumn(record.Document, index.Descriptor.ColumnName);
      if (index.Descriptor.Unique && index.FindConflict(value, record.Header.RecordId) is { } conflict) {
        throw new UniqueConstraintViolationException(collectionName, index.Descriptor.ColumnName,
          Describe(value), conflict);
      }
      index.Add(value, record.Header.RecordId, row.Address);
    }
  }

  //The column's value in a stored document, or the null key when the record does not carry
  //the field at all — an index has to have a place for a record that is missing the value,
  //or the record would drop out of an ordered read of the collection.
  public static IDocumentValue ReadColumn(ObjectDocument document, string columnName) {
    return document.Value is ObjectDocumentValue fields
      ? fields.Values.GetValueOrDefault(columnName) ?? new NullDocumentValue()
      : new NullDocumentValue();
  }

  public static object Describe(IDocumentValue value) {
    return value switch {
      StringDocumentValue text => text.Value,
      IntDocumentValue number => number.Value,
      UIntDocumentValue number => number.Value,
      BooleanDocumentValue flag => flag.Value,
      UlidDocumentValue identifier => identifier.Value,
      _ => null
    };
  }

  private SecondaryIndex Register(IndexDescriptor descriptor) {
    var tree = new BPlusTree(_pageManager, _catalog, _freeSpace, _transactionManager,
      descriptor.CollectionName, new SecondaryIndexRoot(_catalog, descriptor.CollectionName, descriptor.Name));
    var index = new SecondaryIndex(descriptor, tree);
    if (!_byCollection.TryGetValue(descriptor.CollectionName, out var indexes)) {
      indexes = [];
      _byCollection[descriptor.CollectionName] = indexes;
    }
    indexes.Add(index);
    return index;
  }

  private void Append(IndexDescriptor descriptor) {
    var header = RecordHeader.ForNewRecord(descriptor.Id, 1);
    _dataPageManager.WriteRecord(SystemCollections.Indexes, header, IndexDescriptorDocument.Write(descriptor));
  }
}
