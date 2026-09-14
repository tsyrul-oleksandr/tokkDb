using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Query;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Transactions;

namespace TokkDb;

public class DbEntities<T> {
  private readonly DataPageManager _dataPageManager;
  private readonly CollectionCatalog _catalog;
  private readonly TransactionManager _transactionManager;
  private readonly QueryService _queries;
  private readonly DocumentSerializer<T> _serializer;
  private readonly string _entityName;
  private readonly IVersionStore _versionStore;

  public DbEntities(DataPageManager dataPageManager, CollectionCatalog catalog,
      TransactionManager transactionManager, QueryService queries, DocumentSerializer<T> serializer,
      string entityName, IVersionStore versionStore) {
    _dataPageManager = dataPageManager;
    _catalog = catalog;
    _transactionManager = transactionManager;
    _queries = queries;
    _serializer = serializer;
    _entityName = entityName;
    _versionStore = versionStore;
  }

  //HS-1 and V-13: a view of what the catalogue says, never a setting of this handle. It is
  //changed through TokkDbConnection.SetRetentionPolicy, as a schema change.
  public RetentionPolicy RetentionPolicy => _catalog.Get(_entityName).RetentionPolicy;

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

  //RH-1 and V-9. The record's version forest, in write order, with each version's operation
  //beside it, and the shape named: head, leaves, roots and where each root was cut. Empty for a
  //record that keeps no versions or never had one recorded.
  public VersionHistory History(Ulid recordId) {
    var nodes = RetentionPolicy == RetentionPolicy.KeepVersions ? _versionStore.Nodes(_entityName, recordId) : [];
    if (nodes.Count == 0) {
      return new VersionHistory { RecordId = recordId };
    }
    var operations = nodes.Select(node => node.OperationId).Distinct()
      .ToDictionary(id => id, id => _versionStore.Operation(_entityName, id));
    var parents = nodes.Where(node => node.Parent is not null).Select(node => node.Parent!.Value).ToHashSet();
    var head = nodes.MaxBy(node => node.VersionId)!;
    var entries = nodes.Select(node => Entry(node, operations[node.OperationId], node.VersionId == head.VersionId,
      !parents.Contains(node.VersionId))).ToList();
    //V-8: an operation recorded earlier than the one before it, in write order.
    var outOfOrder = new List<Ulid>();
    for (var i = 1; i < entries.Count; i++) {
      var previous = entries[i - 1];
      var current = entries[i];
      if (current.OperationId != previous.OperationId && current.RecordedAt < previous.RecordedAt
          && !outOfOrder.Contains(current.OperationId)) {
        outOfOrder.Add(current.OperationId);
      }
    }
    return new VersionHistory {
      RecordId = recordId,
      Versions = entries,
      Head = head.VersionId,
      IsDeleted = head.Kind == VersionKind.Delete,
      Leaves = entries.Where(entry => entry.IsLeaf).Select(entry => entry.VersionId).ToList(),
      Roots = entries.Where(entry => entry.Parent is null).Select(entry => entry.VersionId).ToList(),
      OperationsRecordedOutOfOrder = outOfOrder
    };
  }

  private static VersionEntry Entry(VersionNode node, Operation operation, bool isHead, bool isLeaf) {
    return new VersionEntry {
      VersionId = node.VersionId, Parent = node.Parent, Kind = node.Kind, SchemaVersion = node.SchemaVersion,
      Distance = node.Distance, IsHead = isHead, IsLeaf = isLeaf, CutFrom = node.CutFrom, ReplacedHead = node.ReplacedHead,
      OperationId = node.OperationId, RecordedAt = operation?.RecordedAt ?? default,
      Author = operation?.Author ?? string.Empty, Cause = operation?.Cause ?? default,
      Comment = operation?.Comment ?? string.Empty
    };
  }

  //RH-2 and V-10. The record at that version, as the current schema reads it: the version as
  //stored, mapped step by step through the schema history between its version and now, with
  //everything the mapping could not carry reported.
  public VersionedValue<T> GetAsOf(Ulid recordId, Ulid versionId) {
    var stored = GetStoredAsOf(recordId, versionId);
    if (stored.IsDeleted) {
      return new VersionedValue<T> { Version = stored.Version, IsDeleted = true };
    }
    var mapped = MapToCurrentSchema(stored.Document, stored.SchemaVersion);
    return new VersionedValue<T> {
      Version = stored.Version,
      Value = _serializer.Deserialize(mapped.Document),
      Unmapped = mapped.Unmapped
    };
  }

  private SchemaMapping.MappedDocument MapToCurrentSchema(ObjectDocument document, ushort schemaVersion) {
    var current = _catalog.Get(_entityName).SchemaVersion;
    return schemaVersion == current
      ? new SchemaMapping.MappedDocument(document, [])
      : SchemaMapping.MapDocument(document, _versionStore.SchemaAt(_entityName, schemaVersion, current));
  }

  //RH-4. The delta between two versions. When to's parent is from and to holds a delta, that
  //delta is the answer, mapped through the schema steps above its version, and no image is
  //read. Otherwise the two versions are reconstructed, each mapped to the current schema, and
  //diffed. asStored skips the mapping: the stored delta as it is, or the diff of the two stored
  //images.
  public VersionDiff Diff(Ulid recordId, Ulid from, Ulid to, bool asStored = false) {
    RequireVersions();
    var readsBefore = _dataPageManager.PageReadCount;
    var target = _versionStore.Node(_entityName, recordId, to) ?? throw new VersionNotFoundException(_entityName, recordId, to);
    if (target.Parent == from && target.Delta is not null) {
      var current = _catalog.Get(_entityName).SchemaVersion;
      var mapped = asStored || target.SchemaVersion == current
        ? new SchemaMapping.MappedDelta(target.Delta, [])
        : SchemaMapping.MapDelta(target.Delta, _versionStore.SchemaAt(_entityName, target.SchemaVersion, current));
      return new VersionDiff {
        From = from, To = to, Delta = mapped.Delta, Unmapped = mapped.Unmapped, FromStoredDelta = true,
        PagesRead = _dataPageManager.PageReadCount - readsBefore
      };
    }
    var before = _versionStore.Reconstruct(_entityName, recordId, from);
    var after = _versionStore.Reconstruct(_entityName, recordId, to);
    if (before.IsDeleted || after.IsDeleted) {
      throw new InvalidOperationException("A tombstone has no document to diff.");
    }
    var unmapped = new List<Unmapped>();
    var left = before.Document;
    var right = after.Document;
    if (!asStored) {
      var mappedLeft = MapToCurrentSchema(before.Document, before.SchemaVersion);
      var mappedRight = MapToCurrentSchema(after.Document, after.SchemaVersion);
      left = mappedLeft.Document;
      right = mappedRight.Document;
      unmapped.AddRange(mappedLeft.Unmapped);
      unmapped.AddRange(mappedRight.Unmapped);
    }
    return new VersionDiff {
      From = from, To = to, Delta = DocumentDiff.Compute(left, right, DiffOptionsFor(_catalog.Get(_entityName))),
      Unmapped = unmapped, FromStoredDelta = false, PagesRead = _dataPageManager.PageReadCount - readsBefore
    };
  }

  //RH-3, V-5 and V-8. The record as it was at a moment of logical time: one Floor search on
  //(recordId, the greatest identifier at or before the moment), and then the version found — or
  //one of four reasons there is none, told apart by what the record's history holds.
  public AsOfResult<T> GetAsOf(Ulid recordId, DateTimeOffset moment) {
    RequireVersions();
    var readsBefore = _dataPageManager.PageReadCount;
    var node = _versionStore.Floor(_entityName, recordId, LogicalTime.AtOrBefore(moment));
    var pagesRead = _dataPageManager.PageReadCount - readsBefore;
    if (node is null) {
      var nodes = _versionStore.Nodes(_entityName, recordId);
      if (nodes.Count == 0) {
        var outcome = _dataPageManager.FindLiveRow(_entityName, recordId) is null
          ? AsOfOutcome.NoSuchRecord
          : AsOfOutcome.BeforeRecordedHistory;
        return new AsOfResult<T> { Outcome = outcome, Moment = moment, PagesRead = pagesRead };
      }
      var root = nodes[0];
      return new AsOfResult<T> {
        Outcome = root.Kind == VersionKind.Baseline || root.CutFrom is not null
          ? AsOfOutcome.BeforeRecordedHistory
          : AsOfOutcome.NotYetCreated,
        Moment = moment, PagesRead = pagesRead
      };
    }
    if (node.Kind == VersionKind.Delete) {
      return new AsOfResult<T> { Outcome = AsOfOutcome.Deleted, Moment = moment, VersionId = node.VersionId, PagesRead = pagesRead };
    }
    return new AsOfResult<T> {
      Outcome = AsOfOutcome.Found, Moment = moment, VersionId = node.VersionId, PagesRead = pagesRead,
      Version = GetAsOf(recordId, node.VersionId)
    };
  }

  //RH-2. The version as stored: reconstructed to its own schema version, with nothing mapped.
  public StoredVersion GetStoredAsOf(Ulid recordId, Ulid versionId) {
    RequireVersions();
    var reconstruction = _versionStore.Reconstruct(_entityName, recordId, versionId);
    var operation = _versionStore.Operation(_entityName, reconstruction.Node.OperationId);
    var head = _versionStore.Head(_entityName, recordId);
    return new StoredVersion {
      Version = Entry(reconstruction.Node, operation, head?.VersionId == versionId, isLeaf: false),
      Document = reconstruction.Document,
      SchemaVersion = reconstruction.SchemaVersion,
      IsDeleted = reconstruction.IsDeleted,
      Report = reconstruction.Report
    };
  }

  private void RequireVersions() {
    if (RetentionPolicy != RetentionPolicy.KeepVersions) {
      throw new InvalidOperationException($"Collection '{_entityName}' keeps no versions.");
    }
  }

  //The version identifier of the record's head: the live image's, or the tombstone's for a
  //deleted record that keeps versions. A caller that needs the version a write produced reads
  //this inside the same unit of work (§3.2).
  public Ulid HeadVersion(Ulid recordId) {
    if (_dataPageManager.FindLiveRow(_entityName, recordId) is { } live) {
      return StoredRecordUtilities.ReadHeader(live.Buffer).VersionId;
    }
    if (RetentionPolicy == RetentionPolicy.KeepVersions && _versionStore.Head(_entityName, recordId) is { } head) {
      return head.VersionId;
    }
    throw new RecordNotFoundException(_entityName, recordId);
  }

  //DC-4: the records carrying a value in an indexed column, read through that index. The
  //column has to be indexed — an unindexed one would be the scan Get already is, and calling
  //it a lookup would hide which of the two the caller got.
  public IEnumerable<DbRecord<T>> GetBy(string columnName, object value) {
    return _dataPageManager.FindRowsByValue(_entityName, columnName, DocumentValues.From(value))
      .Select(row => _dataPageManager.ReadRecord(_entityName, row))
      .Select(record => new DbRecord<T>(record.Header.RecordId, _serializer.Deserialize(record.Document)));
  }

  //DC-5. The query path: the planner picks how to reach the records, and only the records
  //that survive the predicate are turned into values of T. A query over an indexed column
  //reads the index and the pages its entries address; one over an unindexed column scans, and
  //says so in the report rather than looking the same as the other.
  public DbQueryResult<T> Query(NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    return ToResult(_queries.Run(_entityName, query, ids));
  }

  //What the query would do, without doing it.
  public QueryPlan Explain(NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    return _queries.Plan(_entityName, query, ids);
  }

  //The entity query builder (Q-1): a predicate, an order, Skip and Take, and relation steps,
  //collected into one immutable request. See DbQuery.
  public DbQuery<T> Query() {
    return new DbQuery<T>(this);
  }

  //QM-2: a plan Explain returned, executed as it is rather than planned again — or refused with a
  //StalePlanException when the catalogue has changed since it was made.
  public DbQueryResult<T> Run(QueryRequestPlan plan) {
    ArgumentNullException.ThrowIfNull(plan);
    if (plan.CollectionName != _entityName) {
      throw new ArgumentException(
        $"The plan is for collection {plan.CollectionName}, and these entities are {_entityName}.", nameof(plan));
    }
    return ToResult(_queries.Run(plan));
  }

  //A request planned and run under one catalogue lease. What DbQuery.Run does, and what a caller
  //holding a stale plan does to run its request again: entities.Run(plan.Request).
  public DbQueryResult<T> Run(QueryRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    return ToResult(_queries.Run(_entityName, request));
  }

  public QueryRequestPlan Explain(QueryRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    return _queries.Plan(_entityName, request);
  }

  //Deserialized after the query has given its catalogue lease back: turning a stored record into
  //a T reads no page and needs no catalogue.
  private DbQueryResult<T> ToResult(QueryResult result) {
    return new DbQueryResult<T>(
      result.Matches
        .Select(match => new DbRecord<T>(match.Record.Header.RecordId,
          _serializer.Deserialize(match.Record.Document)))
        .ToList(),
      result.Report);
  }

  //The counterpart of Update and Delete, which already address a record by its identity.
  //Still a scan until the primary index of Phase 5 exists, but a scan behind one method
  //rather than in every caller.
  public DbRecord<T> GetById(Ulid recordId) {
    var row = _dataPageManager.FindLiveRow(_entityName, recordId);
    if (row == null) {
      return null;
    }
    var record = _dataPageManager.ReadRecord(_entityName, row.Value);
    return new DbRecord<T>(recordId, _serializer.Deserialize(record.Document));
  }

  //Reading the flags byte is all the skipping of dead images needs; nothing writes a dead
  //image yet, but a scan that ignored the byte would have to change when something does.
  private IEnumerable<StoredRecord> LiveRecords() {
    return _dataPageManager.GetAllRows(_entityName)
      //DC-7: read as the schema now describes it, whatever schema it was written under.
      .Select(row => _dataPageManager.ReadRecord(_entityName, row))
      .Where(record => record.Header.IsLive);
  }

  //VR-12. Copy on write: the image that was current is retired and a new one is written in
  //its place. The record body is never rewritten where it lies.
  //
  //Both happen in one transaction, so a failure anywhere in it — a crash included — leaves
  //the old image exactly where it was and readable, which is what the journal restores.
  //Under KeepVersions the version is recorded in between (WV-2), in the same transaction.
  public void Update(Ulid recordId, T value) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      var current = _dataPageManager.FindLiveRow(_entityName, recordId)
        ?? throw new RecordNotFoundException(_entityName, recordId);
      if (RetentionPolicy == RetentionPolicy.KeepVersions) {
        Supersede(current, value);
      } else {
        //Before the new image exists, so that "the current version" means one thing.
        RemoveCurrentVersion(current, RemoveReason.Superseded);
        WriteImage(recordId, value);
      }
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  //WV-2, in its order. 1: the delta from the head as the current schema reads it to the new
  //document, with the collection's element keys; nothing changed means nothing written. 2 and
  //4, in the store: the head's image copied into its node if that node is a keyframe, and the
  //node for this version, whose identifier sorts after the head's (HS-7). 3: the head's image
  //retired, exactly as under None. 5: the new image, pointing at its node (V-6).
  private void Supersede(DataRow current, T value) {
    var stored = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(current));
    var head = stored.Header;
    var migrator = _dataPageManager.MigratorFor(_entityName, head.SchemaVersion);
    var presented = migrator.IsIdentity ? stored.Document : migrator.Apply(stored.Document);
    var document = _serializer.Create(value, head.RecordId);
    var delta = DocumentDiff.Compute(presented, document, DiffOptionsFor(_catalog.Get(_entityName)));
    if (delta.IsEmpty) {
      return;
    }
    var header = new RecordHeader {
      RecordId = head.RecordId,
      VersionId = RecordIdentity.NextAfter(head.VersionId),
      Flags = RecordFlags.Live,
      SchemaVersion = _catalog.Get(_entityName).SchemaVersion
    };
    header.PreviousVersion = _versionStore.RecordSupersede(_entityName, VersionKind.Update, head, stored.Document,
      header, document, delta, restoredVersion: null);
    RemoveCurrentVersion(current, RemoveReason.Superseded);
    _dataPageManager.WriteRecord(_entityName, header, document);
  }

  //DL-8 and I-5: the element keys the collection declares for its array columns.
  private static DiffOptions DiffOptionsFor(CollectionDescriptor descriptor) {
    var options = DiffOptions.None;
    foreach (var column in descriptor.Columns) {
      if (!string.IsNullOrEmpty(column.ElementKey)) {
        options = options.WithElementKey(column.Name, column.ElementKey);
      }
    }
    return options;
  }

  //WV-3: under KeepVersions the tombstone is recorded first — with the head's image kept where
  //V-1 keeps it — and then the image is retired exactly as under None, index entries and all.
  public void Delete(Ulid recordId) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      var current = _dataPageManager.FindLiveRow(_entityName, recordId)
        ?? throw new RecordNotFoundException(_entityName, recordId);
      if (RetentionPolicy == RetentionPolicy.KeepVersions) {
        var stored = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(current));
        _versionStore.RecordDelete(_entityName, stored.Header, stored.Document);
      }
      RemoveCurrentVersion(current, RemoveReason.Deleted);
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  //VR-12's single entry point for user records: every update and delete retires its image
  //here, and nothing else in the engine retires a user record's image except DropCollection
  //(HS-3). Retirement is the same under every policy; under KeepVersions the version has been
  //recorded before this is reached (WV-2).
  private void RemoveCurrentVersion(DataRow current, RemoveReason reason) {
    var flags = reason == RemoveReason.Deleted ? RecordFlags.Deleted : RecordFlags.Superseded;
    _dataPageManager.RetireRow(_entityName, current.Address, flags);
  }

  //WV-1: under KeepVersions the Insert node is written first, so that the image can point at it.
  private void WriteImage(Ulid recordId, T value) {
    var document = _serializer.Create(value, recordId);
    var header = RecordHeader.ForNewRecord(recordId, _catalog.Get(_entityName).SchemaVersion);
    if (RetentionPolicy == RetentionPolicy.KeepVersions) {
      header.PreviousVersion = _versionStore.RecordInsert(_entityName, header);
    }
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
