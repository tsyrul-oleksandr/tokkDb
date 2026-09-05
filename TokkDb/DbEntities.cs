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

  //D-5: the only policy this pass implements. KeepVersions is declared and refused.
  public RetentionPolicy RetentionPolicy { get; set; } = RetentionPolicy.None;

  public IEnumerable<T> GetAll() {
    return LiveRecords().Select(record => _serializer.Deserialize(record.Document));
  }

  //The values with the identity they are stored under (D-1), which is what Update and Delete
  //take. Nothing else can hand a caller the record identifier.
  public IEnumerable<DbRecord<T>> GetAllRecords() {
    return LiveRecords()
      .Select(record => new DbRecord<T>(record.Header.RecordId, _serializer.Deserialize(record.Document)));
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
      WriteImage(Ulid.NewUlid(), value);
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

  //VR-12. Copy on write: the image that was current is retired and a new one is written in
  //its place. The record body is never rewritten where it lies.
  //
  //Both happen in one transaction, so a failure anywhere in it — a crash included — leaves
  //the old image exactly where it was and readable, which is what the journal restores.
  public void Update(Ulid recordId, T value) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      //Before the new image exists, so that "the current version" means one thing.
      RemoveCurrentVersion(recordId, RemoveReason.Superseded);
      WriteImage(recordId, value);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public void Delete(Ulid recordId) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      RemoveCurrentVersion(recordId, RemoveReason.Deleted);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  //VR-12's single entry point. Every retirement of a record image in the engine goes through
  //here — an update superseding one, a delete removing one — and a version store that wants
  //to keep images instead of dropping them has this one method to intercept.
  private void RemoveCurrentVersion(Ulid recordId, RemoveReason reason) {
    if (RetentionPolicy == RetentionPolicy.KeepVersions) {
      throw new NotSupportedException(
        $"{nameof(RetentionPolicy)}.{nameof(RetentionPolicy.KeepVersions)} is not implemented in this " +
        $"pass (D-5). The retired image would be kept and linked through previousVersion instead.");
    }
    var current = _dataPageManager.FindLiveRow(_entityName, recordId)
      ?? throw new RecordNotFoundException(_entityName, recordId);
    var flags = reason == RemoveReason.Deleted ? RecordFlags.Deleted : RecordFlags.Superseded;
    _dataPageManager.RetireRow(_entityName, current.Address, flags, RetentionPolicy);
  }

  private void WriteImage(Ulid recordId, T value) {
    var document = _serializer.Create(value, recordId);
    var header = RecordHeader.ForNewRecord(recordId, _catalog.Get(_entityName).SchemaVersion);
    var size = StoredRecordUtilities.GetBytesLength(header, document);
    var buffer = _dataPageManager.Register(_entityName, size);
    StoredRecordUtilities.ToBuffer(header, document, buffer);
  }
  
  private bool Filter(ObjectDocument doc, IExpression expression) {
    var result = expression.Execute(doc.Value, doc.Value);
    return result != null;
  }

  public IEnumerable GetHistories() {
    return Array.Empty<object>();
  }
}

//A stored value with the identity it is stored under.
public record DbRecord<T>(Ulid RecordId, T Value);
