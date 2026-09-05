using TokkDb.Documents;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Query;
using TokkDb.Pages.Records;
using TokkDb.Transactions;

namespace TokkDb;

public class DbEntities<T> {
  private readonly DataPageManager _dataPageManager;
  private readonly CollectionCatalog _catalog;
  private readonly TransactionManager _transactionManager;
  private readonly QueryService _queries;
  private readonly DocumentSerializer<T> _serializer;
  private readonly string _entityName;

  public DbEntities(DataPageManager dataPageManager, CollectionCatalog catalog,
      TransactionManager transactionManager, QueryService queries, DocumentSerializer<T> serializer,
      string entityName) {
    _dataPageManager = dataPageManager;
    _catalog = catalog;
    _transactionManager = transactionManager;
    _queries = queries;
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

  //Returns the identity the record was stored under. A caller that has to address the record
  //again — Update, Delete, or an adapter handing the id back to its own caller — would
  //otherwise have to scan for a record it has just written.
  public Ulid Insert(T value) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      //D-1: the identifier the serializer mints is the record identity, and the header
      //carries it rather than a second one beside it. Minted monotonically, because a Ulid
      //is only time-ordered to the millisecond and the primary index wants it ordered
      //within one as well.
      var recordId = RecordIdentity.Next();
      WriteImage(recordId, value);
      transaction.Commit();
      return recordId;
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  //DC-4: the records carrying a value in an indexed column, read through that index. The
  //column has to be indexed — an unindexed one would be the scan Get already is, and calling
  //it a lookup would hide which of the two the caller got.
  public IEnumerable<DbRecord<T>> GetBy(string columnName, object value) {
    return _dataPageManager.FindRowsByValue(_entityName, columnName, DocumentValues.From(value))
      .Select(row => StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row)))
      .Select(record => new DbRecord<T>(record.Header.RecordId, _serializer.Deserialize(record.Document)));
  }

  //DC-5. The query path: the planner picks how to reach the records, and only the records
  //that survive the predicate are turned into values of T. A query over an indexed column
  //reads the index and the pages its entries address; one over an unindexed column scans, and
  //says so in the report rather than looking the same as the other.
  public DbQueryResult<T> Query(NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    var result = _queries.Run(_entityName, query, ids);
    return new DbQueryResult<T>(
      result.Matches
        .Select(match => new DbRecord<T>(match.Record.Header.RecordId,
          _serializer.Deserialize(match.Record.Document)))
        .ToList(),
      result.Report);
  }

  //What the query would do, without doing it.
  public QueryPlan Explain(NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    return _queries.Plan(_entityName, query, ids);
  }

  //The counterpart of Update and Delete, which already address a record by its identity.
  //Still a scan until the primary index of Phase 5 exists, but a scan behind one method
  //rather than in every caller.
  public DbRecord<T> GetById(Ulid recordId) {
    var row = _dataPageManager.FindLiveRow(_entityName, recordId);
    if (row == null) {
      return null;
    }
    var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row.Value));
    return new DbRecord<T>(recordId, _serializer.Deserialize(record.Document));
  }

  //Reading the flags byte is all the skipping of dead images needs; nothing writes a dead
  //image yet, but a scan that ignored the byte would have to change when something does.
  private IEnumerable<StoredRecord> LiveRecords() {
    return _dataPageManager.GetAllRows(_entityName)
      .Select(row => StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row)))
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
    //A record larger than a page keeps its header on the page and its body in an overflow
    //chain (ST-5); which of the two happens is the storage layer's decision. The document
    //goes rather than its bytes, because the indexes of DC-4 are keyed by what is inside it.
    _dataPageManager.WriteRecord(_entityName, header, document);
  }
  }

//A stored value with the identity it is stored under.
public record DbRecord<T>(Ulid RecordId, T Value);

//The records a query returned and what reading them cost (UI-4). The report travels with the
//result rather than beside it, so a caller cannot read the records without being able to say
//how they were reached.
public record DbQueryResult<T>(IReadOnlyList<DbRecord<T>> Records, QueryReport Report);
