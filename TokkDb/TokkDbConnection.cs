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
  private readonly MetadataPageManager _metadataPageManager;

  public TokkDbConnection(string filePath) {
    _diskManager = new DiskManager(filePath);
    var pageManager = new PageManager(_diskManager);
    _transactionManager = new TransactionManager(pageManager);
    _rootPageManager = new RootPageManager(pageManager, _transactionManager);
    _metadataPageManager = new MetadataPageManager(pageManager, _rootPageManager, _transactionManager);
    _dataPageManager = new DataPageManager(pageManager, _metadataPageManager, _transactionManager);
  }

  public bool IsExists() {
    return !_diskManager.IsBlank();
  }

  public void Load() {
    InTransaction(Initialize);
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
      foreach (var entity in config.Entities) {
        _metadataPageManager.CreateEntity(entity.Key);
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
    _metadataPageManager.Initialize();
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
