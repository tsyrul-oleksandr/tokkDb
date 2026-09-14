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
    //HS-7, before anything in this process mints against this database: the source of every
    //identifier is raised to the greatest one stored, and never lowered.
    RecordIdentity.RaiseTo(Get(SystemCollections.Collections).LastIdentifier);
    CreateMissingSystemCollections();
  }

  //HS-7's high-water mark. Called by every outermost commit that has pages to write, before
  //its journal frame is written: if any identifier minted since the mark was last stored is
  //above it, the catalogue's own descriptor is saved with the new mark, and Save stamps the mark
  //after minting the descriptor's own header, so nothing minted in the transaction escapes it.
  //A transaction that minted nothing finds the mark where it was and dirties no page for it.
  public void RecordIdentifierMark() {
    if (!_descriptors.TryGetValue(SystemCollections.Collections, out var catalogue) || catalogue.Address is null) {
      return;
    }
    if (RecordIdentity.Last.CompareTo(catalogue.LastIdentifier) > 0) {
      Save(catalogue);
    }
  }

  //D-4 said the reserved list would grow — "(later: _events, _versions)" — so a database
  //written before a system collection existed has to gain it rather than be migrated. It
  //costs one catalogue document, and a collection nothing has written to has no pages at all.
  private void CreateMissingSystemCollections() {
    foreach (var name in SystemCollections.All.Where(name => !Exists(name))) {
      CreateCollectionCore(name, SystemCollectionColumns(name), SystemCollections.Descriptions[name]);
    }
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

  //V-4 and HS-2. The internal creation and drop paths for a history collection, which carries
  //the reserved prefix and so cannot go through CreateCollection or DropCollection. The refusal
  //of reserved names stays whole for everything else: these two accept one prefix, and only
  //the version store calls them.
  public CollectionDescriptor CreateHistoryCollection(string name, IEnumerable<ColumnDescriptor> columns,
      string description) {
    RequireHistoryName(name);
    return CreateCollectionCore(name, columns, description);
  }

  public bool DropHistoryCollection(string name) {
    _transactionManager.RequireTransaction();
    RequireHistoryName(name);
    return DropCollectionCore(name);
  }

  private static void RequireHistoryName(string name) {
    if (!Versions.HistoryCollections.IsHistoryName(name)) {
      throw new ArgumentException(
        $"'{name}' is not a history collection name: those begin with '{Versions.HistoryCollections.Prefix}'.",
        nameof(name));
    }
  }

  //V-4: the link from a versioned collection to its history collection, default when it has
  //none.
  public void SetHistoryCollectionId(string collectionName, Ulid historyCollectionId) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    descriptor.HistoryCollectionId = historyCollectionId;
    Save(descriptor);
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

  //V-5: where a history collection's version index begins, kept with the other physical
  //pointers of its descriptor (D-2).
  public void SetVersionIndexRoot(string historyCollectionName, uint pageIndex) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(historyCollectionName);
    descriptor.VersionIndexRoot = pageIndex;
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

  //The column set of a reserved collection, for one whose documents are defined above the
  //engine. It is the counterpart of the CreateSelfColumns the engine's own system collections
  //carry, and it exists so that a collection like _semanticTypes — whose shape only the
  //application knows — still describes itself in the catalogue rather than only in code
  //(DC-7). The schema version does not move: nothing here changes what is stored, it records
  //what was already being stored.
  public void DescribeSystemCollection(string collectionName, IEnumerable<ColumnDescriptor> columns) {
    _transactionManager.RequireTransaction();
    if (!SystemCollections.IsReservedName(collectionName)) {
      throw new ArgumentException(
        $"'{collectionName}' is not a system collection.", nameof(collectionName));
    }
    var descriptor = Get(collectionName);
    descriptor.Columns = columns?.ToList() ?? [];
    Save(descriptor);
  }

  //DC-7. The column set of a collection, replaced as a whole and the schema version bumped
  //with it. Records already written keep the version they were written under (VR-11), which
  //is what makes the migration lazy: a read decides what an old record means from the version
  //in its header rather than the collection being rewritten.
  public CollectionDescriptor SetColumns(string collectionName, IEnumerable<ColumnDescriptor> columns,
      IEnumerable<ColumnMigration> migrations = null) {
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
    //Stamped with the version they produced, which is what a read compares the record's own
    //version against. The caller says what changed rather than the catalogue diffing the two
    //column sets: a rename and a remove-then-add look identical in a diff and mean opposite
    //things to a record written before either.
    foreach (var migration in migrations ?? []) {
      migration.Version = descriptor.SchemaVersion;
      descriptor.Migrations.Add(migration);
    }
    Save(descriptor);
    return descriptor;
  }

  //HS-1 and V-13. The retention settings of a collection, in its catalogue document and
  //nowhere else. A reserved collection cannot keep versions (F-6): the catalogue would have to
  //be readable before its own history. The interval is at least 1 — k = 1 is the full-copy
  //layout — and the ratio is in (0, 1]: a delta larger than its image never happens, and a
  //ratio of 0 would make every version a keyframe by a rule meant for the exceptional ones.
  public CollectionDescriptor SetRetentionPolicy(string collectionName, RetentionPolicy policy,
      int snapshotInterval, double largeDeltaRatio) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    if (descriptor.IsSystem && policy != RetentionPolicy.None) {
      throw new ReservedCollectionNameException(collectionName);
    }
    if (snapshotInterval < 1) {
      throw new ArgumentOutOfRangeException(nameof(snapshotInterval), snapshotInterval,
        "The keyframe interval is at least 1.");
    }
    if (!(largeDeltaRatio > 0 && largeDeltaRatio <= 1)) {
      throw new ArgumentOutOfRangeException(nameof(largeDeltaRatio), largeDeltaRatio,
        "The large-delta ratio is in (0, 1].");
    }
    descriptor.RetentionPolicy = policy;
    descriptor.SnapshotInterval = snapshotInterval;
    descriptor.LargeDeltaRatio = largeDeltaRatio;
    Save(descriptor);
    return descriptor;
  }

  //Every record is at the current version, so there is nothing left for a read to replay.
  //Rewrite calls this last, after the records have converged — before that the steps are the
  //only thing that can read them.
  public void ClearMigrations(string collectionName) {
    _transactionManager.RequireTransaction();
    var descriptor = Get(collectionName);
    if (descriptor.Migrations.Count == 0) {
      return;
    }
    descriptor.Migrations.Clear();
    Save(descriptor);
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
    return DropCollectionCore(collectionName);
  }

  private bool DropCollectionCore(string collectionName) {
    if (!_descriptors.TryGetValue(collectionName, out var descriptor)) {
      return false;
    }
    if (descriptor.Address is { } address) {
      _dataPageManager.RetireRow(SystemCollections.Collections, address, RecordFlags.Deleted);
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
      CreateCollectionCore(name, SystemCollectionColumns(name), SystemCollections.Descriptions[name]);
    }
  }

  //The system collections that hold descriptors describe their own columns, for the same
  //reason _collections does: nothing about the catalogue should be readable only in code.
  //One whose documents are defined above the engine says so through DescribeSystemCollection.
  private static List<ColumnDescriptor> SystemCollectionColumns(string name) {
    return name switch {
      SystemCollections.Collections => CollectionDescriptorDocument.CreateSelfColumns(),
      SystemCollections.Indexes => IndexDescriptorDocument.CreateColumns(),
      SystemCollections.Relations => RelationDescriptorDocument.CreateColumns(),
      SystemCollections.DisplayRules => DisplayRuleDocument.CreateColumns(),
      SystemCollections.Settings => SettingsDocument.CreateColumns(),
      _ => []
    };
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
    StampIdentifierMark(descriptor);
    //Written through the same path as any other record, so a descriptor that outgrew a page
    //would take an overflow chain like anything else.
    var row = _dataPageManager.WriteRecord(SystemCollections.Collections, header,
      CollectionDescriptorDocument.Write(descriptor));
    //The record count moved while the row was being made; write what the descriptor says now.
    _dataPageManager.UpdateRow(row.Address, header, CollectionDescriptorDocument.Write(descriptor));
    descriptor.Address = row.Address;
  }

  //HS-7: the catalogue's own descriptor carries the mark, taken after its header was minted so
  //that the header's own version identifier is under it.
  private static void StampIdentifierMark(CollectionDescriptor descriptor) {
    if (descriptor.Name == SystemCollections.Collections) {
      descriptor.LastIdentifier = RecordIdentity.Last;
    }
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

  //Set while the catalogue's own descriptor is being moved to a new slot. Moving it can need
  //a new catalogue page, and a new page is recorded on that same descriptor — a save of the
  //descriptor from inside its own move, into a slot it has just left. The inner save is
  //deferred, and the move writes the descriptor again once it has landed, in place, because
  //what changed meanwhile (the last page, the free-space root) is fixed-width.
  private bool _movingSelf;
  private bool _selfChangedWhileMoving;

  protected virtual void Save(CollectionDescriptor descriptor) {
    if (descriptor.Address is null) {
      //Not written yet: the append in progress will put the current values on the page.
      return;
    }
    var self = descriptor.Name == SystemCollections.Collections;
    if (self && _movingSelf) {
      _selfChangedWhileMoving = true;
      return;
    }
    var header = CreateHeader(descriptor);
    StampIdentifierMark(descriptor);
    var document = CollectionDescriptorDocument.Write(descriptor);
    //A descriptor grows: gaining a secondary index adds a root to it (DC-4), and the slot it
    //was first written into was sized for the descriptor as it then was. An image that no
    //longer fits where it lies moves to a slot that holds it.
    if (_dataPageManager.CanUpdateRowInPlace(descriptor.Address.Value, header, document)) {
      _dataPageManager.UpdateRow(descriptor.Address.Value, header, document);
      return;
    }
    if (!self) {
      descriptor.Address = _dataPageManager
        .RewriteRow(SystemCollections.Collections, descriptor.Address.Value, header, document).Address;
      return;
    }
    _movingSelf = true;
    _selfChangedWhileMoving = false;
    try {
      descriptor.Address = MoveSelf(descriptor.Address.Value, header, document);
    } finally {
      _movingSelf = false;
    }
    if (_selfChangedWhileMoving) {
      _selfChangedWhileMoving = false;
      Save(descriptor);
    }
  }

  //The catalogue's own descriptor, moved to a slot that holds it (see _movingSelf).
  private DocumentAddress MoveSelf(DocumentAddress address, RecordHeader header, ObjectDocument document) {
    return _dataPageManager.RewriteRow(SystemCollections.Collections, address, header, document).Address;
  }
}
