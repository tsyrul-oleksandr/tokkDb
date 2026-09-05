using TokkDb.Documents;
using TokkDb.Pages.Managers;
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

  protected virtual void LoadCatalog() {
    //Just enough of a descriptor to find the catalogue's own pages. Every other field of
    //every collection, this one included, comes out of the documents below.
    _descriptors[SystemCollections.Collections] = CreateBootstrapDescriptor();
    var rows = _dataPageManager.GetAllRows(SystemCollections.Collections).ToList();
    foreach (var row in rows) {
      var record = StoredRecordUtilities.FromBuffer(row.Buffer);
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
      var columns = name == SystemCollections.Collections
        ? CollectionDescriptorDocument.CreateSelfColumns()
        : [];
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
      Id = Ulid.NewUlid(),
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
    } catch {
      _descriptors.Remove(name);
      throw;
    }
    return descriptor;
  }

  //Identifiers are never reused, so a page left behind by a dropped collection can never be
  //mistaken for a page of a new one.
  protected virtual uint GetNewOwningCollectionId() {
    return _descriptors.Count == 0 ? 1 : _descriptors.Values.Max(descriptor => descriptor.OwningCollectionId) + 1;
  }

  //Writing the first descriptor is what allocates the catalogue's first page and points the
  //root page at it, so this runs before the descriptor has an address.
  protected virtual void Append(CollectionDescriptor descriptor) {
    //The catalogue's records carry the VR-11 header like any other, and the descriptor's own
    //identifier is the record identity (D-1) rather than a second one beside it.
    var header = CreateHeader(descriptor);
    var length = StoredRecordUtilities.GetBytesLength(header, CollectionDescriptorDocument.Write(descriptor));
    var row = _dataPageManager.RegisterRow(SystemCollections.Collections, length);
    //The record count moved while the row was being made; write what the descriptor says now.
    StoredRecordUtilities.ToBuffer(header, CollectionDescriptorDocument.Write(descriptor), row.Buffer);
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
    _dataPageManager.UpdateRow(descriptor.Address.Value, CreateHeader(descriptor),
      CollectionDescriptorDocument.Write(descriptor));
  }
}
