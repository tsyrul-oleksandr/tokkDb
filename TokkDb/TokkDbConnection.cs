using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb;

//Holds the database file open for as long as it lives, so it has to be disposed before the
//same file is opened again.
public class TokkDbConnection : IDisposable {
  private readonly DiskManager _diskManager;
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly RootPageManager _rootPageManager;
  private readonly CollectionCatalog _catalog;

  public TokkDbConnection(string filePath) {
    _diskManager = new DiskManager(filePath);
    var pageManager = new PageManager(_diskManager);
    _transactionManager = new TransactionManager(pageManager);
    _rootPageManager = new RootPageManager(pageManager, _transactionManager);
    _catalog = new CollectionCatalog(_rootPageManager);
    _dataPageManager = new DataPageManager(pageManager, _catalog, _transactionManager);
    _catalog.SetDataPageManager(_dataPageManager);
  }

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
    InTransaction(() => descriptor = _catalog.CreateCollection(name, columns, description));
    return descriptor;
  }

  public CollectionDescriptor CreateCollection<T>(string name = null, string description = "") {
    return CreateCollection(name ?? typeof(T).Name, EntityColumns.Describe(typeof(T)), description);
  }

  public DbEntities<T> Entities<T>(string name = null) {
    name ??= typeof(T).Name;
    var serializer = new DocumentSerializer<T>();
    return new DbEntities<T>(_dataPageManager, _transactionManager, serializer, name);
  }

  public void CreateDatabase(Action<TokkDbConfiguration> configure) {
    InTransaction(() => {
      Initialize();
      var config = new TokkDbConfiguration();
      configure(config);
      foreach (var (name, entity) in config.Entities) {
        _catalog.CreateCollection(name, EntityColumns.Describe(entity.EntityType), entity.Description);
      }
    });
  }

  public void Dispose() {
    _diskManager.Dispose();
  }

  //Reading the root page first is what tells the rest of the engine the page size and where
  //the catalogue is; on a blank file it is what creates them.
  private void Initialize() {
    _rootPageManager.Initialize();
    _catalog.Initialize();
  }

  private void InTransaction(Action action) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      action();
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }
}
