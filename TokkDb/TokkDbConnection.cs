using Microsoft.Extensions.Logging;
using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Relations;
using TokkDb.Pages.Managers;
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
    _dataPageManager.SetCatalogs(_indexCatalog, _relationCatalog);
  }

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
    return new DbEntities<T>(_dataPageManager, _catalog, _transactionManager, serializer, name);
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
      string targetCollection, string targetColumn) {
    RelationDescriptor descriptor = null;
    InTransaction(() => descriptor =
      _relationCatalog.Create(name, sourceCollection, sourceColumn, targetCollection, targetColumn));
    return descriptor;
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
      _indexCatalog.Create(descriptor.Name, column.Name, unique: true);
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
    //The free-space structures and the index trees hang off the catalogue, so they are stale
    //the moment it is reloaded and are read again from their roots on first use.
    _freeSpace.Reset();
    _dataPageManager.Reset();
  }

}
