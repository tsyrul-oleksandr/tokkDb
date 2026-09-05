using Microsoft.Extensions.Logging;
using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Relations;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Query;
using TokkDb.Transactions;

namespace TokkDb;

//Holds the database file open for as long as it lives, so it has to be disposed before the
//same file is opened for writing again.
//
//Isolation (TX-4): one writer at a time, any number of readers alongside it. A writer holds
//an exclusive lock beside the database and a second one is refused with
//DatabaseLockedException; a reader takes no lock and cannot write.
public class TokkDbConnection : IDisposable {
  private readonly DiskManager _diskManager;
  private readonly PageManager _pageManager;
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly RootPageManager _rootPageManager;
  private readonly CollectionCatalog _catalog;
  private readonly IndexCatalog _indexCatalog;
  private readonly RelationCatalog _relationCatalog;
  private readonly FreeSpaceManager _freeSpace;
  private readonly QueryService _queries;
  private readonly CollectionSettingsCatalog _settings;

  public TokkDbConnection(string filePath, TokkDbAccessMode accessMode = TokkDbAccessMode.ReadWrite,
      ILogger logger = null)
    : this(new DiskManager(filePath, accessMode: accessMode, logger: logger)) { }

  //Takes an already opened file. The connection owns it from here and disposes it.
  public TokkDbConnection(DiskManager diskManager) {
    _diskManager = diskManager;
    //TX-2: before any page of this database is read by anything.
    RecoveryDecision = _diskManager.Recover();
    _pageManager = new PageManager(_diskManager);
    _transactionManager = new TransactionManager(_pageManager);
    _rootPageManager = new RootPageManager(_pageManager, _transactionManager);
    _catalog = new CollectionCatalog(_rootPageManager, _transactionManager);
    _freeSpace = new FreeSpaceManager(_pageManager, _rootPageManager, _catalog, _transactionManager);
    _dataPageManager = new DataPageManager(_pageManager, _catalog, _freeSpace, _transactionManager);
    _indexCatalog = new IndexCatalog(_pageManager, _catalog, _freeSpace, _transactionManager);
    _relationCatalog = new RelationCatalog(_catalog, _indexCatalog, _transactionManager);
    _catalog.SetDataPageManager(_dataPageManager);
    _indexCatalog.SetDataPageManager(_dataPageManager);
    _relationCatalog.SetDataPageManager(_dataPageManager);
    _settings = new CollectionSettingsCatalog(_transactionManager);
    _settings.SetDataPageManager(_dataPageManager);
    _dataPageManager.SetCatalogs(_indexCatalog, _relationCatalog);
    _queries = new QueryService(_dataPageManager, _indexCatalog, _pageManager);
  }

  //DC-5 and UI-4: the planner, and the report every query it runs publishes. A host that
  //wants the measurements subscribes to QueryExecuted here rather than at each call site.
  public QueryService Queries => _queries;

  //Physical page reads since the file was opened. A catalogue lookup must not move it.
  public long PageReadCount => _diskManager.PageReadCount;

  //What recovery found and did when this connection opened the file.
  public RecoveryDecision RecoveryDecision { get; }

  public TokkDbAccessMode AccessMode => _diskManager.AccessMode;

  public bool IsExists() {
    return !_diskManager.IsBlank();
  }

  public void Load() {
    InTransaction(Initialize);
  }
  
  //The catalogue as it was read at open. Every collection in the database is here,
  //_collections included, and _collections describes itself.
  public IReadOnlyCollection<CollectionDescriptor> Collections => _catalog.Descriptors;

  public CollectionDescriptor Collection(string name) {
    return _catalog.Get(name);
  }

  //Creating a collection appends a document to _collections; the reserved "_" prefix is
  //refused here just as it is in the configuration passed to CreateDatabase.
  public CollectionDescriptor CreateCollection(string name, IEnumerable<ColumnDescriptor> columns = null,
      string description = "") {
    CollectionDescriptor descriptor = null;
    InTransaction(() => {
      descriptor = _catalog.CreateCollection(name, columns, description);
      CreateUniqueIndexes(descriptor);
    });
    return descriptor;
  }

  public CollectionDescriptor CreateCollection<T>(string name = null, string description = "") {
    return CreateCollection(name ?? typeof(T).Name, EntityColumns.Describe(typeof(T)), description);
  }

  public DbEntities<T> Entities<T>(string name = null) {
    return Entities(new DocumentSerializer<T>(), name);
  }

  //A caller whose records are not a fixed CLR type — the IStorage adapter, whose records are
  //field maps described by a collection definition — supplies its own serializer rather than
  //being given the reflection-over-properties one.
  public DbEntities<T> Entities<T>(DocumentSerializer<T> serializer, string name = null) {
    name ??= typeof(T).Name;
    return new DbEntities<T>(_dataPageManager, _catalog, _transactionManager, _queries, serializer, name);
  }

  //DC-4: the secondary indexes and the referential constraints, as the catalogue holds them.
  public IEnumerable<IndexDescriptor> Indexes => _indexCatalog.Descriptors;

  public IEnumerable<RelationDescriptor> Relations => _relationCatalog.Descriptors;

  //An index over one column. Building it reads the collection once; after that nothing does.
  public IndexDescriptor CreateIndex(string collectionName, string columnName, bool unique = false) {
    IndexDescriptor descriptor = null;
    InTransaction(() => descriptor = _indexCatalog.Create(collectionName, columnName, unique).Descriptor);
    return descriptor;
  }

  //DC-4: a relation cannot be checked without an index on the column it points at, so
  //creating one creates that index if it is not already there.
  public RelationDescriptor CreateRelation(string name, string sourceCollection, string sourceColumn,
      string targetCollection, string targetColumn, string cardinality = "", string description = "") {
    RelationDescriptor descriptor = null;
    InTransaction(() => descriptor = _relationCatalog.Create(name, sourceCollection, sourceColumn,
      targetCollection, targetColumn, cardinality, description));
    return descriptor;
  }

  //D-4: the display rule and the per-collection settings, as their own documents. The engine
  //stores both and interprets neither.
  public string DisplayRule(string collectionName) {
    return _settings.GetDisplayRule(collectionName);
  }

  public void SetDisplayRule(string collectionName, string template) {
    InTransaction(() => _settings.SetDisplayRule(collectionName, template));
  }

  public IReadOnlyDictionary<string, string> Metadata(string collectionName) {
    return _settings.GetMetadata(collectionName);
  }

  public void SetMetadata(string collectionName, IReadOnlyDictionary<string, string> metadata) {
    InTransaction(() => _settings.SetMetadata(collectionName, metadata));
  }

  //DC-7. Replaces the column set of a collection and bumps its schema version. The indexes
  //over columns that are gone go with them, because an index over a column nothing declares
  //could never be chosen by the planner and would still be maintained on every write.
  public CollectionDescriptor SetColumns(string collectionName, IEnumerable<ColumnDescriptor> columns) {
    CollectionDescriptor descriptor = null;
    InTransaction(() => {
      var wanted = columns?.ToList() ?? [];
      foreach (var index in _indexCatalog.For(collectionName).ToArray()) {
        var column = wanted.FirstOrDefault(candidate => candidate.Name == index.Descriptor.ColumnName);
        //Dropped, or no longer unique: a unique index that outlived the declaration would go
        //on refusing duplicates the schema now permits.
        if (column is null || (index.Descriptor.Unique && !column.Unique)) {
          _indexCatalog.Drop(collectionName, index.Descriptor.ColumnName);
        }
      }
      descriptor = _catalog.SetColumns(collectionName, wanted);
      CreateUniqueIndexes(descriptor);
    });
    return descriptor;
  }

  //Removes a collection and everything the engine holds about it: its records, its indexes,
  //its relations, its display rule and its settings, in one transaction (DC-8).
  public bool DropCollection(string collectionName) {
    var dropped = false;
    InTransaction(() => {
      if (!_catalog.Exists(collectionName)) {
        return;
      }
      foreach (var relation in _relationCatalog.Naming(collectionName)) {
        _relationCatalog.Remove(relation.Name);
      }
      _indexCatalog.DropAll(collectionName);
      //Before the descriptor goes: retiring a row asks the catalogue where the collection's
      //pages are.
      foreach (var row in _dataPageManager.GetAllRows(collectionName).ToArray()) {
        _dataPageManager.RetireRow(collectionName, row.Address, RecordFlags.Deleted, RetentionPolicy.None);
      }
      _settings.Remove(collectionName);
      dropped = _catalog.DropCollection(collectionName);
    });
    return dropped;
  }

  public bool DropIndex(string collectionName, string columnName) {
    var dropped = false;
    InTransaction(() => dropped = _indexCatalog.Drop(collectionName, columnName));
    return dropped;
  }

  public bool RemoveRelation(string name) {
    var removed = false;
    InTransaction(() => removed = _relationCatalog.Remove(name));
    return removed;
  }

  //DC-4: the collection's primary index. The tree reads its own root out of the catalogue
  //document (D-2), so this hands back a view of what is on disk rather than a structure that
  //had to be built first.
  public BPlusTree PrimaryIndex(string collectionName) {
    return _dataPageManager.PrimaryIndex(collectionName);
  }

  //Runs the action inside a transaction, so a caller driving the index directly gets the
  //same journal and the same rollback as everything else.
  public void InTransaction(Action action) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      action();
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public void CreateDatabase(Action<TokkDbConfiguration> configure) {
    InTransaction(() => {
      Initialize();
      var config = new TokkDbConfiguration();
      configure(config);
      foreach (var (name, entity) in config.Entities) {
        CreateUniqueIndexes(
          _catalog.CreateCollection(name, EntityColumns.Describe(entity.EntityType), entity.Description));
      }
    });
  }

  public void Dispose() {
    _diskManager.Dispose();
  }

  //DC-4: a column declared unique is enforced by a unique index, and there is nowhere else
  //the enforcement could live — the check is a lookup by value, which is what an index is.
  private void CreateUniqueIndexes(CollectionDescriptor descriptor) {
    foreach (var column in descriptor.Columns.Where(column => column.Unique)) {
      //A column set that is being replaced mostly keeps the columns it had, so most of the
      //unique ones already have the index this would create.
      if (_indexCatalog.Find(descriptor.Name, column.Name) is null) {
        _indexCatalog.Create(descriptor.Name, column.Name, unique: true);
      }
    }
  }

  //Reading the root page first is what tells the rest of the engine the page size and where
  //the catalogue is; on a blank file it is what creates them.
  private void Initialize() {
    _rootPageManager.Initialize();
    _catalog.Initialize();
    //After the collections, because an index descriptor names the collection it covers, and
    //before anything is written, because a write maintains whatever is described here.
    _indexCatalog.Initialize();
    _relationCatalog.Initialize();
    //Neither structural: what a collection displays as and what the application notes about
    //it are their own documents (D-4), so a change to either leaves the schema alone.
    _settings.Initialize();
    //The free-space structures and the index trees hang off the catalogue, so they are stale
    //the moment it is reloaded and are read again from their roots on first use.
    _freeSpace.Reset();
    _dataPageManager.Reset();
  }

}
