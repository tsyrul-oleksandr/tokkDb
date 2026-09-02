using System.Collections;
using TokkDb.Documents;
using TokkDb.Documents.Path;
using TokkDb.Documents.Path.Expressions;
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
  
  public IEnumerable<T> Get(string exp) {
    var expression = DocumentPathParser.Parse(exp);
    return _dataPageManager.GetAll(_entityName).Select(ObjectDocumentUtilities.FromBuffer)
      .Where(doc => Filter(doc, expression)).Select(_serializer.Deserialize);
  }

  public void Insert(T value) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      var document = _serializer.Create(value, Ulid.NewUlid());
      var size = ObjectDocumentUtilities.GetBytesLength(document);
      var buffer = _dataPageManager.Register(_entityName, size);
      ObjectDocumentUtilities.ToBuffer(document, buffer);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public void Update(T value, string condition) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      //var document = _serializer.Create(value);
      //var size = ObjectDocumentUtilities.GetBytesLength(document);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }
  
  private bool Filter(ObjectDocument doc, IExpression expression) {
    var result = expression.Execute(doc.Value, doc.Value);
    return result != null;
  }

  public IEnumerable GetHistories() {
    return Array.Empty<object>();
  }
}
