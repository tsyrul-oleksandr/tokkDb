using TokkDb.Documents;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Records;
using TokkDb.Pages.Relations;
using TokkDb.Transactions;

namespace TokkDb.Pages;

//The catalogue of D-4. Collection definitions are documents in "_collections", stored
//through the same page, document and transaction machinery as user data, and cached in
//memory from open so that reading a field costs nothing.
public class CollectionCatalog {
  private readonly RootPageManager _rootPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly Dictionary<string, CollectionDescriptor> _descriptors = new(StringComparer.Ordinal);

  //The knot D-4 creates: the catalogue is a collection like any other, so it reads and
  //writes itself through the manager that asks it where the pages of a collection are.
  private DataPageManager _dataPageManager;

  //The transaction manager is here for DC-8: a catalogue change has to refuse before it
  //touches anything, not fail somewhere downstream once the cache has already moved.
  public CollectionCatalog(RootPageManager rootPageManager, TransactionManager transactionManager) {
    _rootPageManager = rootPageManager;
    _transactionManager = transactionManager;
  }

  public IReadOnlyCollection<CollectionDescriptor> Descriptors => _descriptors.Values;

  public void SetDataPageManager(DataPageManager dataPageManager) {
    _dataPageManager = dataPageManager;
  }

  //The root page has to be initialized first: it says whether a catalogue exists at all.
  public void Initialize() {
    _descriptors.Clear();
    if (_rootPageManager.CollectionsFirstPageId == default) {
      CreateNewCatalog();
      return;
    }
    LoadCatalog();
  }

  public bool Exists(string collectionName) {
    return _descriptors.ContainsKey(collectionName);
  }

  public CollectionDescriptor Get(string collectionName) {
    return _descriptors.GetValueOrDefault(collectionName)
      ?? throw new EntityNotFoundException($"Collection {collectionName} not found");
  }

  //The public way in. A name beginning with "_" belongs to the engine and is refused here.
  public CollectionDescriptor CreateCollection(string name, IEnumerable<ColumnDescriptor> columns = null,
      string description = "") {
    if (SystemCollections.IsReservedName(name)) {
      throw new ReservedCollectionNameException(name);
    }
    return CreateCollectionCore(name, columns, description);
  }

  public uint GetOwningCollectionId(string collectionName) {
    return Get(collectionName).OwningCollectionId;
  }

  public uint GetDataFirstPage(string collectionName) {
    return Get(collectionName).DataFirstPage;
  }

  public uint GetDataLastPage(string collectionName) {
    return Get(collectionName).DataLastPage;
  }

  //ST-1: where the collection's free-space structure begins.
  public void SetFreeSpaceRoot(string collectionName, uint pageIndex) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    descriptor.FreeSpaceRoot = pageIndex;
    Save(descriptor);
  }

  //D-2: the root of the collection's primary index is a physical pointer, so it lives in the
  //catalogue document beside the data chain and the free-space root, and moves with them
  //inside the same transaction when the tree grows a new root.
  public void SetPrimaryIndexRoot(string collectionName, uint pageIndex) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    descriptor.PrimaryIndexRoot = pageIndex;
    Save(descriptor);
  }

  //DC-4: where one of the collection's secondary indexes begins. The descriptor of the index
  //itself lives in _indexes; this is the physical pointer D-2 keeps in the catalogue.
  public void SetSecondaryIndexRoot(string collectionName, string indexName, uint pageIndex) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    descriptor.SecondaryIndexRoots[indexName] = pageIndex;
    Save(descriptor);
  }

  //The counterpart of SetSecondaryIndexRoot, for an index that no longer exists. The entry is
  //removed rather than zeroed: a root of zero is what an empty tree has, so leaving the name
  //behind would describe an index that is merely empty rather than gone.
  public void RemoveSecondaryIndexRoot(string collectionName, string indexName) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    if (descriptor.SecondaryIndexRoots.Remove(indexName)) {
      Save(descriptor);
    }
  }

  public void SetDataLastPage(string collectionName, uint pageIndex) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    descriptor.DataLastPage = pageIndex;
    if (descriptor.DataFirstPage == default) {
      descriptor.DataFirstPage = pageIndex;
      if (collectionName == SystemCollections.Collections) {
        //Page 0 is the only place that can say where the catalogue itself begins.
        _rootPageManager.SetCollectionsFirstPageId(pageIndex);
      }
    }
    Save(descriptor);
  }

  public void IncrementRecordCount(string collectionName) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    descriptor.RecordCount++;
    Save(descriptor);
  }

  public void DecrementRecordCount(string collectionName) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    if (descriptor.RecordCount > 0) {
      descriptor.RecordCount--;
    }
    Save(descriptor);
  }

  public uint AllocatePageIndex() {
    return _rootPageManager.AllocatePageIndex();
  }

  //DC-7. The column set of a collection, replaced as a whole and the schema version bumped
  //with it. Records already written keep the version they were written under (VR-11), which
  //is what makes the migration lazy: a read decides what an old record means from the version
  //in its header rather than the collection being rewritten.
  public CollectionDescriptor SetColumns(string collectionName, IEnumerable<ColumnDescriptor> columns) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    if (descriptor.IsSystem) {
      throw new ReservedCollectionNameException(collectionName);
    }
    descriptor.Columns = columns?.ToList() ?? [];
    //ushort, so it stops rather than wraps to a version that already means something else.
    if (descriptor.SchemaVersion < ushort.MaxValue) {
      descriptor.SchemaVersion++;
    }
    Save(descriptor);
    return descriptor;
  }

  //Removes a collection from the catalogue. The caller is responsible for what the collection
  //held — its records, its indexes and the relations naming it — because the catalogue knows
  //about none of them.
  //
  //The pages the collection occupied are not returned to anything. Free space is per
  //collection (ST-1) and there is no global free-page list, so its pages stay allocated and
  //unreachable until a file-level compaction exists to reclaim them. The alternative is a
  //global free list, which is a storage change rather than a catalogue one.
  public bool DropCollection(string collectionName) {
    _transactionManager.RequireTransaction();
    if (SystemCollections.IsReservedName(collectionName)) {
      throw new ReservedCollectionNameException(collectionName);
    }
    if (!_descriptors.TryGetValue(collectionName, out var descriptor)) {
      return false;
    }
    if (descriptor.Address is { } address) {
      _dataPageManager.RetireRow(SystemCollections.Collections, address, RecordFlags.Deleted,
        RetentionPolicy.None);
    }
    _descriptors.Remove(collectionName);
    return true;
  }

  protected virtual void LoadCatalog() {
    //Just enough of a descriptor to find the catalogue's own pages. Every other field of
    //every collection, this one included, comes out of the documents below.
    _descriptors[SystemCollections.Collections] = CreateBootstrapDescriptor();
    var rows = _dataPageManager.GetAllRows(SystemCollections.Collections).ToList();
    foreach (var row in rows) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (!record.Header.IsLive) {
        continue;
      }
      var descriptor = CollectionDescriptorDocument.Read(record.Document);
      descriptor.Address = row.Address;
      //The stored _collections describes itself and replaces the bootstrap stub.
      _descriptors[descriptor.Name] = descriptor;
    }
  }

  protected virtual void CreateNewCatalog() {
    foreach (var name in SystemCollections.All) {
      //The system collections that hold descriptors describe their own columns, for the same
      //reason _collections does: nothing about the catalogue should be readable only in code.
      var columns = name switch {
        SystemCollections.Collections => CollectionDescriptorDocument.CreateSelfColumns(),
        SystemCollections.Indexes => IndexDescriptorDocument.CreateColumns(),
        SystemCollections.Relations => RelationDescriptorDocument.CreateColumns(),
        SystemCollections.DisplayRules => DisplayRuleDocument.CreateColumns(),
        SystemCollections.Settings => SettingsDocument.CreateColumns(),
        _ => []
      };
      CreateCollectionCore(name, columns, SystemCollections.Descriptions[name]);
    }
  }

  //The hardcoded minimal descriptor D-4 allows, and the only one in the engine. It carries
  //no columns: what _collections looks like is read from its own document.
  protected virtual CollectionDescriptor CreateBootstrapDescriptor() {
    return new CollectionDescriptor {
      Name = SystemCollections.Collections,
      DataFirstPage = _rootPageManager.CollectionsFirstPageId
    };
  }

  protected virtual CollectionDescriptor CreateCollectionCore(string name, IEnumerable<ColumnDescriptor> columns,
      string description) {
    //DC-8: the descriptor document and the pages it points at commit together or not at all.
    _transactionManager.RequireTransaction();
    if (Exists(name)) {
      throw new ArgumentException($"Collection {name} already exists", nameof(name));
    }
    var descriptor = new CollectionDescriptor {
      Id = RecordIdentity.Next(),
      Name = name,
      Description = description,
      Columns = columns?.ToList() ?? [],
      OwningCollectionId = GetNewOwningCollectionId()
    };
    //_collections has to be findable while its own first document is being written, so the
    //cache goes first and is wound back if the write does not happen.
    _descriptors[name] = descriptor;
    try {
      Append(descriptor);
      RecordOwningCollectionId(descriptor.OwningCollectionId);
    } catch {
      _descriptors.Remove(name);
      throw;
    }
    return descriptor;
  }

  //Identifiers are never reused, so a page left behind by a dropped collection can never be
  //mistaken for a page of a new one.
  //
  //The high-water mark is what makes that true across a drop. The maximum of the collections
  //that exist falls when the newest one is dropped, and the pages it left behind are still in
  //the file carrying its id — so the next collection would be handed the id written on them.
  protected virtual uint GetNewOwningCollectionId() {
    var highest = _descriptors.Values
      .Select(descriptor => descriptor.OwningCollectionId)
      .Append(HighWaterMark())
      .Max();
    return highest + 1;
  }

  private uint HighWaterMark() {
    return _descriptors.TryGetValue(SystemCollections.Collections, out var catalogue)
      ? catalogue.LastOwningCollectionId
      : 0;
  }

  //Recorded on the catalogue's own descriptor, which is a document like any other, so the
  //mark survives a reopen without a new place to keep it (D-4, DC-7).
  private void RecordOwningCollectionId(uint owningCollectionId) {
    if (!_descriptors.TryGetValue(SystemCollections.Collections, out var catalogue)
        || catalogue.LastOwningCollectionId >= owningCollectionId) {
      return;
    }
    catalogue.LastOwningCollectionId = owningCollectionId;
    Save(catalogue);
  }

  //Writing the first descriptor is what allocates the catalogue's first page and points the
  //root page at it, so this runs before the descriptor has an address.
  protected virtual void Append(CollectionDescriptor descriptor) {
    //The catalogue's records carry the VR-11 header like any other, and the descriptor's own
    //identifier is the record identity (D-1) rather than a second one beside it.
    var header = CreateHeader(descriptor);
    //Written through the same path as any other record, so a descriptor that outgrew a page
    //would take an overflow chain like anything else.
    var row = _dataPageManager.WriteRecord(SystemCollections.Collections, header,
      CollectionDescriptorDocument.Write(descriptor));
    //The record count moved while the row was being made; write what the descriptor says now.
    _dataPageManager.UpdateRow(row.Address, header, CollectionDescriptorDocument.Write(descriptor));
    descriptor.Address = row.Address;
  }

  //A fresh version identifier on every write, as VR-11 requires, even though nothing reads
  //it until versioning exists.
  protected virtual RecordHeader CreateHeader(CollectionDescriptor descriptor) {
    return RecordHeader.ForNewRecord(descriptor.Id, GetCatalogSchemaVersion());
  }

  private ushort GetCatalogSchemaVersion() {
    return _descriptors.TryGetValue(SystemCollections.Collections, out var catalogue)
      ? catalogue.SchemaVersion
      : (ushort)1;
  }

  protected virtual void Save(CollectionDescriptor descriptor) {
    if (descriptor.Address is null) {
      //Not written yet: the append in progress will put the current values on the page.
      return;
    }
    var header = CreateHeader(descriptor);
    var document = CollectionDescriptorDocument.Write(descriptor);
    //A descriptor grows: gaining a secondary index adds a root to it (DC-4), and the slot it
    //was first written into was sized for the descriptor as it then was. An image that no
    //longer fits where it lies moves to a slot that holds it.
    if (_dataPageManager.CanUpdateRowInPlace(descriptor.Address.Value, header, document)) {
      _dataPageManager.UpdateRow(descriptor.Address.Value, header, document);
      return;
    }
    descriptor.Address = _dataPageManager
      .RewriteRow(SystemCollections.Collections, descriptor.Address.Value, header, document).Address;
  }
}
