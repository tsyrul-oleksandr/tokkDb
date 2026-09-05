using System.Collections;
using TokkDb.Documents;
using TokkDb.Documents.Path;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb;

public class DbEntities<T> {
  private readonly DataPageManager _dataPageManager;
  private readonly CollectionCatalog _catalog;
  private readonly TransactionManager _transactionManager;
  private readonly DocumentSerializer<T> _serializer;
  private readonly string _entityName;

  public DbEntities(DataPageManager dataPageManager, CollectionCatalog catalog,
      TransactionManager transactionManager, DocumentSerializer<T> serializer, string entityName) {
    _dataPageManager = dataPageManager;
    _catalog = catalog;
    _transactionManager = transactionManager;
    _serializer = serializer;
    _entityName = entityName;
  }

  public IEnumerable<T> GetAll() {
    return LiveRecords().Select(record => _serializer.Deserialize(record.Document));
  }
  
  public IEnumerable<T> Get(string exp) {
    var expression = DocumentPathParser.Parse(exp);
    return LiveRecords().Where(record => Filter(record.Document, expression))
      .Select(record => _serializer.Deserialize(record.Document));
  }

  public void Insert(T value) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      //D-1: the identifier the serializer mints is the record identity, and the header
      //carries it rather than a second one beside it.
      var recordId = Ulid.NewUlid();
      var document = _serializer.Create(value, recordId);
      var header = RecordHeader.ForNewRecord(recordId, _catalog.Get(_entityName).SchemaVersion);
      var size = StoredRecordUtilities.GetBytesLength(header, document);
      var buffer = _dataPageManager.Register(_entityName, size);
      StoredRecordUtilities.ToBuffer(header, document, buffer);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  //Reading the flags byte is all the skipping of dead images needs; nothing writes a dead
  //image yet, but a scan that ignored the byte would have to change when something does.
  private IEnumerable<StoredRecord> LiveRecords() {
    return _dataPageManager.GetAll(_entityName)
      .Select(StoredRecordUtilities.FromBuffer)
      .Where(record => record.Header.IsLive);
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
