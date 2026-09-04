using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb;

public class TokkDbConnection {
  private readonly DiskManager _diskManager;
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly MetadataPageManager _metadataPageManager;
  public TokkDbConnection(string filePath) {
    _diskManager = new DiskManager(filePath);
    var pageManager = new PageManager(_diskManager);
    _transactionManager = new TransactionManager(pageManager);
    _metadataPageManager = new MetadataPageManager(pageManager, _transactionManager);
    _dataPageManager = new DataPageManager(pageManager, _metadataPageManager, _transactionManager);
  }

  public bool IsExists() {
    return !_diskManager.IsBlank();
  }

  public void Load() {
    _metadataPageManager.Initialize();
  }
  
  public DbEntities<T> Entities<T>(string name = null) {
    name ??= typeof(T).Name;
    var serializer = new DocumentSerializer<T>();
    return new DbEntities<T>(_dataPageManager, _transactionManager, serializer, name);
  }

  public void CreateDatabase(Action<TokkDbConfiguration> configure) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      _metadataPageManager.Initialize();
      var config = new TokkDbConfiguration();
      configure(config);
      foreach (var entity in config.Entities) {
        _metadataPageManager.CreateEntity(entity.Key);
      }
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
    
  }
}
