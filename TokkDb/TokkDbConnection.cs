using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb;

public class TokkDbConnection {
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly MetadataPageManager _metadataPageManager;
  public TokkDbConnection(string filePath) {
    var pageManager = new PageManager(new DiskManager(filePath));
    _transactionManager = new TransactionManager(pageManager);
    _metadataPageManager = new MetadataPageManager(pageManager, _transactionManager);
    _metadataPageManager.Initialize();
    _dataPageManager = new DataPageManager(pageManager, _metadataPageManager, _transactionManager);
  }
  
  public DbEntities<T> Entities<T>(string name) {
    if (!_metadataPageManager.IsExist(name)) {
      var transaction = _transactionManager.CreateTransaction();
      try {
        _metadataPageManager.CreateEntity(name);
        transaction.Commit();
      } catch {
        transaction.Rollback();
        throw;
      }
    }
    var serializer = new DocumentSerializer<T>();
    return new DbEntities<T>(_dataPageManager, _transactionManager, serializer, name);
  }
}
