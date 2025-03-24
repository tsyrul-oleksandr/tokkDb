using System.Collections;
using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb;

public class DbEntities<T> {
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly DocumentSerializer<T> _serializer;
  private readonly string _entityName;

  public DbEntities(DataPageManager dataPageManager, TransactionManager transactionManager, 
      DocumentSerializer<T> serializer, string entityName) {
    _dataPageManager = dataPageManager;
    _transactionManager = transactionManager;
    _serializer = serializer;
    _entityName = entityName;
  }

  public IEnumerable<T> GetAll() {
    return _dataPageManager.GetAll(_entityName).Select(ObjectDocumentUtilities.FromBuffer).Select(_serializer.Deserialize);
  }

  public void Insert(T value) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      var document = _serializer.Serialize(value);
      var size = ObjectDocumentUtilities.GetBytesLength(document);
      var buffer = _dataPageManager.Register(_entityName, size);
      ObjectDocumentUtilities.ToBuffer(document, buffer);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public void UpdateById(T value, object key) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      var document = _serializer.Serialize(value);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public void Save() {
    
  }

  public IEnumerable GetHistories() {
    return Array.Empty<object>();
  }
}
