using Microsoft.Extensions.Logging;
using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Relations;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Query;
using TokkDb.Pages.Versions;
using TokkDb.Transactions;

namespace TokkDb;

//Holds the database file open for as long as it lives, so it has to be disposed before the
//same file is opened for writing again.
//
//Isolation (TX-4): one writer at a time, any number of readers alongside it. A writer holds
//an exclusive lock beside the database and a second one is refused with
//DatabaseLockedException; a reader takes no lock and cannot write.
public class TokkDbConnection : IDisposable {
  private readonly DiskManager _diskManager;
  private readonly PageManager _pageManager;
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly RootPageManager _rootPageManager;
  private readonly CollectionCatalog _catalog;
  private readonly IndexCatalog _indexCatalog;
  private readonly RelationCatalog _relationCatalog;
  private readonly FreeSpaceManager _freeSpace;
  private readonly QueryService _queries;
  private readonly CollectionSettingsCatalog _settings;
  private readonly SystemDocumentStore _systemDocuments;
  private readonly IVersionStore _versionStore;
  private readonly CatalogLock _catalogLock = new();
  private VersionAttribution _attribution;

  public TokkDbConnection(string filePath, TokkDbAccessMode accessMode = TokkDbAccessMode.ReadWrite,
      ILogger logger = null)
    : this(new DiskManager(filePath, accessMode: accessMode, logger: logger)) { }

  //Takes an already opened file. The connection owns it from here and disposes it.
  public TokkDbConnection(DiskManager diskManager) {
    _diskManager = diskManager;
    //TX-2: before any page of this database is read by anything.
    RecoveryDecision = _diskManager.Recover();
    _pageManager = new PageManager(_diskManager);
    _transactionManager = new TransactionManager(_pageManager);
    _rootPageManager = new RootPageManager(_pageManager, _transactionManager);
    _catalog = new CollectionCatalog(_rootPageManager, _transactionManager);
    _freeSpace = new FreeSpaceManager(_pageManager, _rootPageManager, _catalog, _transactionManager);
    _dataPageManager = new DataPageManager(_pageManager, _catalog, _freeSpace, _transactionManager);
    _indexCatalog = new IndexCatalog(_pageManager, _catalog, _freeSpace, _transactionManager);
    _relationCatalog = new RelationCatalog(_catalog, _indexCatalog, _transactionManager);
    _catalog.SetDataPageManager(_dataPageManager);
    _indexCatalog.SetDataPageManager(_dataPageManager);
    _relationCatalog.SetDataPageManager(_dataPageManager);
    _systemDocuments = new SystemDocumentStore(_transactionManager);
    _systemDocuments.SetDataPageManager(_dataPageManager);
    _settings = new CollectionSettingsCatalog(_transactionManager, _systemDocuments);
    _dataPageManager.SetCatalogs(_indexCatalog, _relationCatalog);
    //Layer 1 (§3): the one place a VersionStore is made; everything reaches it through the contract.
    _versionStore = new VersionStore(_catalog, _dataPageManager, _transactionManager, _pageManager, _freeSpace,
      _relationCatalog, _catalogLock);
    //HS-7: every outermost commit records the identifier high-water mark before its frame.
    _transactionManager.BeforeOutermostCommit = _catalog.RecordIdentifierMark;
    //HS-9: every outermost transaction is stamped with the attribution scope open when it begins.
    _transactionManager.AttributionSource = () => _attribution;
    _transactionManager.AfterOutermostRollback = ReloadCatalogues;
    _queries = new QueryService(_dataPageManager, _catalog, _indexCatalog, _relationCatalog, _pageManager,
      _catalogLock, _freeSpace);
  }

  //Layer 1's contract (§3.1), for the layers above it and for tests. Everything a caller may
  //do with history goes through it.
  public IVersionStore Versions => _versionStore;

  //V-12 and HS-9. Opens a scope that stamps every outermost unit of work beginning inside it
  //with who is making the change and why; the operation those units record carries it (HS-6).
  //One scope at a time: a nested one would leave it unclear which attribution a unit of work
  //carries, so it is refused rather than silently ignored.
  public IDisposable Attribute(VersionAttribution attribution) {
    ArgumentNullException.ThrowIfNull(attribution);
    if (_attribution is not null) {
      throw new InvalidOperationException(
        "An attribution scope is already open on this connection; dispose it before opening another.");
    }
    _attribution = attribution;
    return new AttributionScope(this);
  }

  private sealed class AttributionScope(TokkDbConnection connection) : IDisposable {
    public void Dispose() {
      connection._attribution = null;
    }
  }

  //DC-5 and UI-4: the planner, and the report every query it runs publishes. A host that
  //wants the measurements subscribes to QueryExecuted here rather than at each call site.
  public QueryService Queries => _queries;

  //Physical page reads since the file was opened. A catalogue lookup must not move it.
  public long PageReadCount => _diskManager.PageReadCount;

  //What recovery found and did when this connection opened the file.
  public RecoveryDecision RecoveryDecision { get; }

  public TokkDbAccessMode AccessMode => _diskManager.AccessMode;

  public bool IsExists() {
    return !_diskManager.IsBlank();
  }

  public void Load() {
    ChangeSchema(Initialize);
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
    ChangeSchema(() => {
      descriptor = _catalog.CreateCollection(name, columns, description);
      CreateUniqueIndexes(descriptor);
      KeepVersionsByDefault(descriptor);
    });
    return descriptor;
  }

  //V-13 and I-4: a user collection keeps versions from the moment it exists, at the interval
  //and ratio step 5.1 measured and step 5.2 accepted (k = 8, ratio 0.5), with its history
  //collection created in the same transaction as the collection itself. Existing collections
  //keep what they have; reserved ones never keep versions.
  private void KeepVersionsByDefault(CollectionDescriptor descriptor) {
    _catalog.SetRetentionPolicy(descriptor.Name, RetentionPolicy.KeepVersions,
      CollectionDescriptor.DefaultSnapshotInterval, CollectionDescriptor.DefaultLargeDeltaRatio);
    _versionStore.CreateHistory(descriptor.Name);
  }

  public CollectionDescriptor CreateCollection<T>(string name = null, string description = "") {
    return CreateCollection(name ?? typeof(T).Name, EntityColumns.Describe(typeof(T)), description);
  }

  public DbEntities<T> Entities<T>(string name = null) {
    return Entities(new DocumentSerializer<T>(), name);
  }

  //A caller whose records are not a fixed CLR type — the IStorage adapter, whose records are
  //field maps described by a collection definition — supplies its own serializer rather than
  //being given the reflection-over-properties one.
  public DbEntities<T> Entities<T>(DocumentSerializer<T> serializer, string name = null) {
    name ??= typeof(T).Name;
    return new DbEntities<T>(_dataPageManager, _catalog, _transactionManager, _queries, serializer, name,
      _versionStore);
  }

  //DC-4: the secondary indexes and the referential constraints, as the catalogue holds them.
  public IEnumerable<IndexDescriptor> Indexes => _indexCatalog.Descriptors;

  public IEnumerable<RelationDescriptor> Relations => _relationCatalog.Descriptors;

  //An index over one column. Building it reads the collection once; after that nothing does.
  public IndexDescriptor CreateIndex(string collectionName, string columnName, bool unique = false) {
    IndexDescriptor descriptor = null;
    ChangeSchema(() => descriptor = _indexCatalog.Create(collectionName, columnName, unique).Descriptor);
    return descriptor;
  }

  //DC-4: a relation cannot be checked without an index on the column it points at, so
  //creating one creates that index if it is not already there.
  public RelationDescriptor CreateRelation(string name, string sourceCollection, string sourceColumn,
      string targetCollection, string targetColumn, string cardinality = "", string description = "") {
    RelationDescriptor descriptor = null;
    ChangeSchema(() => {
      descriptor = _relationCatalog.Create(name, sourceCollection, sourceColumn, targetCollection, targetColumn,
        cardinality, description);
      RecordRelationChange(descriptor, RelationNodeKind.Created);
    });
    return descriptor;
  }

  //HS-10: a relation node in the history of every versioned collection the relation names —
  //both of them, or the one when a collection refers to itself — except a collection whose
  //history is going with it.
  private void RecordRelationChange(RelationDescriptor relation, RelationNodeKind kind, string dropping = null) {
    foreach (var name in new[] { relation.SourceCollection, relation.TargetCollection }.Distinct()) {
      if (name != dropping && _catalog.Exists(name)) {
        _versionStore.RecordRelation(name,
          kind == RelationNodeKind.Created ? RelationNode.Created(relation) : RelationNode.Removed(relation));
      }
    }
  }

  //D-4: the reserved collections, as documents. A layer that needs to keep something in the
  //catalogue — the semantic type registry does — defines the document and writes it here;
  //there is no second storage mechanism to build, which is the whole point of Option B.
  public SystemDocumentStore SystemDocuments => _systemDocuments;

  //Declares what the documents of a reserved collection look like. Only the engine's own
  //system collections describe themselves out of the box; one whose documents are defined a
  //layer up says so through this, so that nothing in the catalogue is readable only in code
  //(DC-7).
  public void DescribeSystemCollection(string collectionName, IEnumerable<ColumnDescriptor> columns) {
    ChangeSchema(() => _catalog.DescribeSystemCollection(collectionName, columns));
  }

  //D-4: the display rule and the per-collection settings, as their own documents. The engine
  //stores both and interprets neither.
  public string DisplayRule(string collectionName) {
    return _settings.GetDisplayRule(collectionName);
  }

  public void SetDisplayRule(string collectionName, string template) {
    InTransaction(() => _settings.SetDisplayRule(collectionName, template));
  }

  public IReadOnlyDictionary<string, string> Metadata(string collectionName) {
    return _settings.GetMetadata(collectionName);
  }

  public void SetMetadata(string collectionName, IReadOnlyDictionary<string, string> metadata) {
    InTransaction(() => _settings.SetMetadata(collectionName, metadata));
  }

  //HS-1 and V-13: what a collection keeps of its retired images, with the keyframe interval and
  //the large-delta ratio of V-1. A schema change (QM-2b), because the seam reads it on every
  //write. The defaults are I-1 and I-2 as accepted at step 5.2, which new user collections get
  //without asking. A reserved collection, an interval below 1 and a ratio outside (0, 1] are
  //refused before anything is written.
  //
  //V-4 and V-14: turning KeepVersions on creates the collection's history collection in the
  //same transaction. Turning it off while that history exists is refused unless dropHistory is
  //true, and then the whole history is dropped and None set in one transaction — history with
  //an unrecorded gap would claim a lineage it does not have (F-1).
  public CollectionDescriptor SetRetentionPolicy(string collectionName, RetentionPolicy policy,
      int snapshotInterval = CollectionDescriptor.DefaultSnapshotInterval,
      double largeDeltaRatio = CollectionDescriptor.DefaultLargeDeltaRatio, bool dropHistory = false) {
    CollectionDescriptor descriptor = null;
    ChangeSchema(() => {
      var current = _catalog.Get(collectionName);
      if (policy == RetentionPolicy.None && current.HistoryCollectionId != default && !dropHistory) {
        throw new InvalidOperationException(
          $"Collection '{collectionName}' keeps versions and has a history. Turning versioning off drops " +
          $"that history: call {nameof(SetRetentionPolicy)} with {nameof(dropHistory)}: true to drop it " +
          $"(V-14).");
      }
      descriptor = _catalog.SetRetentionPolicy(collectionName, policy, snapshotInterval, largeDeltaRatio);
      if (policy == RetentionPolicy.KeepVersions && descriptor.HistoryCollectionId == default) {
        _versionStore.CreateHistory(collectionName);
      } else if (policy == RetentionPolicy.None && descriptor.HistoryCollectionId != default) {
        _versionStore.DropHistory(collectionName);
      }
    });
    return descriptor;
  }

  //RP-1, V-15 and I-7. Applies V-15's rule to every record of the collection, one record per
  //transaction, so that an interrupted purge has lost nothing but the records it had not
  //reached and is run again to finish (RP-1). The records come in batches of identifiers, read
  //ahead of the transactions that purge them so that no scan is held open across a change to
  //the index; the batch size is I-7's default. The collection-wide purge ends with one pass
  //that removes every operation document no remaining node references (V-12). Runs outside
  //any unit of work, because each record is one of its own.
  public PurgeReport PurgeHistory(string collectionName, DateTimeOffset before, int batchSize = DefaultPurgeBatchSize) {
    if (_catalog.Get(collectionName).RetentionPolicy != RetentionPolicy.KeepVersions) {
      throw new InvalidOperationException($"Collection '{collectionName}' keeps no versions.");
    }
    return new HistoryPurge(_versionStore, _dataPageManager, _transactionManager)
      .PurgeCollection(collectionName, before, batchSize);
  }

  //RP-7 and VR-10. The history's counts and sizes, by one scan; NF-2, NF-4 and NF-5 are
  //computed from them.
  public HistoryReport HistoryReport(string collectionName) {
    return _versionStore.Report(collectionName);
  }

  //RP-8 and NFR-5. WV-10's invariants first, by the store's scan; then every node of every
  //record is reconstructed, so that a stored old value that no longer matches (DL-7) is
  //reported at the node whose delta fails, and a node that reaches its image only beyond its
  //stored distance is reported too. A larger stored distance, which a purge leaves behind, is
  //allowed. The first failing node is named, and the walk stops at it.
  public HistoryVerification VerifyHistory(string collectionName) {
    var invariants = _versionStore.Verify(collectionName);
    if (!invariants.IsSound || _catalog.Get(collectionName).HistoryCollectionId == default) {
      return invariants;
    }
    Ulid? after = null;
    while (true) {
      var batch = _versionStore.NextRecords(collectionName, after, DefaultPurgeBatchSize);
      foreach (var recordId in batch) {
        foreach (var node in _versionStore.Nodes(collectionName, recordId)) {
          if (node.Kind == VersionKind.Delete) {
            continue;
          }
          try {
            var reconstruction = _versionStore.Reconstruct(collectionName, recordId, node.VersionId);
            var stepsUp = reconstruction.Report.VersionsExamined - 1;
            if (stepsUp > node.Distance) {
              return Failure(invariants, recordId, node.VersionId,
                $"version {node.VersionId} of record {recordId} reaches an image {stepsUp} steps up, beyond its stored distance {node.Distance}");
            }
          } catch (Documents.Delta.DeltaMismatchException mismatch) {
            var failing = mismatch.Version ?? node.VersionId;
            return Failure(invariants, recordId, failing, $"version {failing} of record {recordId} does not reconstruct: {mismatch.Message}");
          } catch (Exception exception) when (exception is not OutOfMemoryException) {
            return Failure(invariants, recordId, node.VersionId, $"version {node.VersionId} of record {recordId} does not reconstruct: {exception.Message}");
          }
        }
      }
      if (batch.Count < DefaultPurgeBatchSize) {
        return invariants;
      }
      after = batch[^1];
    }
  }

  private static HistoryVerification Failure(HistoryVerification counted, Ulid recordId, Ulid versionId, string problem) {
    return new HistoryVerification {
      Problems = [problem], Records = counted.Records, Nodes = counted.Nodes, IndexEntries = counted.IndexEntries,
      FailingRecord = recordId, FailingVersion = versionId
    };
  }

  //I-7, settled at step 7.1: how many record identifiers a collection-wide purge reads ahead
  //of the transactions that purge them. A batch costs one range read of that many identifiers
  //and 16 bytes each in memory; it bounds neither a transaction, which is one record, nor the
  //purge, which walks every batch.
  public const int DefaultPurgeBatchSize = 256;

  //RH-9 and V-11. The columns and relations of a versioned collection as declared at a moment
  //of logical time, from the schema and relation nodes in memory: the last schema node at or
  //before the moment, and every relation whose last node at or before the moment created it.
  //Before the first schema node there is no recorded history to answer from.
  public SchemaSnapshot SchemaAsOf(string collectionName, DateTimeOffset moment) {
    return SchemaSnapshots.At(_versionStore, collectionName, moment);
  }

  //DC-7. Replaces the column set of a collection and bumps its schema version. The indexes
  //over columns that are gone go with them, because an index over a column nothing declares
  //could never be chosen by the planner and would still be maintained on every write.
  //DC-7. Replaces the column set, records what changed so that records written under the old
  //one can still be read (lazy migration), and rebuilds the indexes the change invalidated —
  //all inside the one transaction, so a failure leaves the schema, the log and the indexes as
  //they were.
  //
  //The indexes are rebuilt eagerly while the records are not, and the asymmetry is the point.
  //A query encodes its constant as the column's current type, so an index still holding keys
  //made from the old one would answer wrongly — there is no lazy version of that. But an index
  //is keys, not records: rebuilding one reads the collection and writes a fraction of it,
  //leaving no dead space in the data pages, which is what rewriting every record would cost.
  public CollectionDescriptor SetColumns(string collectionName, IEnumerable<ColumnDescriptor> columns,
      IEnumerable<ColumnMigration> migrations = null) {
    CollectionDescriptor descriptor = null;
    ChangeSchema(() => {
      var wanted = columns?.ToList() ?? [];
      var steps = migrations?.ToList() ?? [];
      //Which columns the change touches, under the names the indexes currently use.
      var touched = steps
        .Select(step => step.ColumnName)
        .ToHashSet(StringComparer.Ordinal);
      //A non-unique index over a touched column is rebuilt rather than lost, under whatever
      //the column is called afterwards. A unique one is recreated by CreateUniqueIndexes from
      //the declaration, so it does not need remembering here.
      var rebuild = new List<string>();
      foreach (var index in _indexCatalog.For(collectionName).ToArray()) {
        var name = index.Descriptor.ColumnName;
        var column = wanted.FirstOrDefault(candidate => candidate.Name == name);
        var renamedTo = steps
          .FirstOrDefault(step => step.Kind == ColumnMigrationKind.Rename && step.ColumnName == name)?.NewName;
        //Dropped, or no longer unique, or over a column the change touched: a unique index
        //that outlived the declaration would go on refusing duplicates the schema now permits,
        //and one built from the old type holds keys a query can no longer match.
        if (column is null || renamedTo is not null || touched.Contains(name)
            || (index.Descriptor.Unique && !column.Unique)) {
          if (!index.Descriptor.Unique && (renamedTo ?? (column is null ? null : name)) is { } survivor) {
            rebuild.Add(survivor);
          }
          _indexCatalog.Drop(collectionName, name);
        }
      }
      descriptor = _catalog.SetColumns(collectionName, wanted, steps);
      //HS-10: the schema version this change produced, recorded in the same transaction for a
      //collection that keeps versions.
      _versionStore.RecordSchema(collectionName, SchemaNode.Of(descriptor, steps));
      //After the descriptor, so the build reads records through the migration that has just
      //been recorded and indexes the values the columns now mean.
      CreateUniqueIndexes(descriptor);
      foreach (var name in rebuild) {
        if (wanted.Any(column => column.Name == name) && _indexCatalog.Find(collectionName, name) is null) {
          _indexCatalog.Create(collectionName, name);
        }
      }
    });
    return descriptor;
  }

  //DC-7's eager migration, offered rather than imposed. Brings every record of the collection
  //up to the current schema and then drops the log, after which a read replays nothing.
  //
  //Deliberately not one transaction. A collection of any size would hold every one of its
  //pages dirty until the commit, and the point of lazy migration is that this cost is the
  //caller's to schedule. Batching is safe because the work is idempotent and partial progress
  //is exactly the state lazy migration already handles: a record that was converged is at the
  //current version, one that was not is read through the log, and the log is only dropped once
  //nothing is left below the current version.
  //
  //Returns how many records it rewrote.
  //
  //WV-8: one of the four paths that may change a versioned record's stored image (HS-3). It
  //keeps the record's VersionId and PreviousVersion and creates no version. Before it migrates
  //a head whose stored image the migration would lose something of — a removed column's
  //value, a retyped one — it stores that image in the head's node, writing the head's Baseline
  //when it has none and pointing the header at it: the one case in which Rewrite moves a
  //pointer, from zero. Whether anything would be lost is what V-10's mapping says, value by
  //value.
  public int Rewrite(string collectionName, int batchSize = 500) {
    var descriptor = _catalog.Get(collectionName);
    if (descriptor.Migrations.Count == 0) {
      return 0;
    }
    var versioned = descriptor.RetentionPolicy == RetentionPolicy.KeepVersions;
    var rewritten = 0;
    while (true) {
      var batch = PendingRows(collectionName, batchSize);
      if (batch.Count == 0) {
        break;
      }
      InTransaction(() => {
        foreach (var (address, _) in batch) {
          //Re-read inside the transaction: an earlier batch may have moved the record.
          var page = _dataPageManager.LiveRowAt(address);
          if (page is not { } row) {
            continue;
          }
          var stored = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
          var header = stored.Header;
          if (versioned && WouldLoseSomething(collectionName, stored)) {
            var node = _versionStore.PreserveImage(collectionName, header, stored.Document);
            if (header.PreviousVersion == default) {
              header.PreviousVersion = node;
            }
          }
          var record = _dataPageManager.ReadRecord(collectionName, row);
          header.SchemaVersion = descriptor.SchemaVersion;
          _dataPageManager.MigrateRow(collectionName, row, header, record.Document);
          rewritten++;
        }
      });
    }
    //Last, and only now: the log is what made every record before this readable. A schema change,
    //because a query reading an old record reads it through this log.
    ChangeSchema(() => _catalog.ClearMigrations(collectionName));
    return rewritten;
  }

  //WV-8 and V-10: whether migrating this stored image to the current schema would leave
  //anything the mapping reports as unmapped — a removed column's value, a lossy retype.
  private bool WouldLoseSomething(string collectionName, StoredRecord stored) {
    var descriptor = _catalog.Get(collectionName);
    if (stored.Header.SchemaVersion >= descriptor.SchemaVersion) {
      return false;
    }
    var steps = _versionStore.SchemaAt(collectionName, stored.Header.SchemaVersion, descriptor.SchemaVersion);
    return SchemaMapping.MapDocument(stored.Document, steps).Unmapped.Count > 0;
  }

  //The records still below the current version. Read outside the transaction that rewrites
  //them, because rewriting a record can move it and the walk would then be following pages
  //that are changing underneath it.
  private List<(DocumentAddress Address, ushort Version)> PendingRows(string collectionName, int batchSize) {
    var version = _catalog.Get(collectionName).SchemaVersion;
    var pending = new List<(DocumentAddress, ushort)>();
    foreach (var row in _dataPageManager.GetAllRows(collectionName)) {
      var header = StoredRecordUtilities.ReadHeader(row.Buffer);
      if (header.IsLive && header.SchemaVersion < version) {
        pending.Add((row.Address, header.SchemaVersion));
        if (pending.Count == batchSize) {
          break;
        }
      }
    }
    return pending;
  }

  //Removes a collection and everything the engine holds about it: its records, its indexes,
  //its relations, its display rule, its settings and its history, in one transaction (DC-8,
  //HS-2). One of the four paths that may change a versioned record's stored image (HS-3): it
  //removes the records together with their history, so the history stays consistent by being
  //gone.
  public bool DropCollection(string collectionName) {
    var dropped = false;
    ChangeSchema(() => {
      if (!_catalog.Exists(collectionName)) {
        return;
      }
      //V-17: the frame of this transaction would hold every record and node it retires.
      _transactionManager.RequireTransaction().MarkForFrameDiscard();
      foreach (var relation in _relationCatalog.Naming(collectionName)) {
        _relationCatalog.Remove(relation.Name);
        RecordRelationChange(relation, RelationNodeKind.Removed, dropping: collectionName);
      }
      _indexCatalog.DropAll(collectionName);
      //Through the store, so that every node is retired rather than the entry dropped alone.
      _versionStore.DropHistory(collectionName);
      //Before the descriptor goes: retiring a row asks the catalogue where the collection's
      //pages are.
      foreach (var row in _dataPageManager.GetAllRows(collectionName).ToArray()) {
        _dataPageManager.RetireRow(collectionName, row.Address, RecordFlags.Deleted);
      }
      //RP-4: the primary index's pages are cleared like a dropped secondary index's. Its keys
      //are identifiers rather than values, but the rule is every page an index occupied.
      _dataPageManager.PrimaryIndex(collectionName).ReleasePages();
      _settings.Remove(collectionName);
      dropped = _catalog.DropCollection(collectionName);
      //A collection created later under the same name starts with pages of its own.
      _freeSpace.Forget(collectionName);
    });
    return dropped;
  }

  public bool DropIndex(string collectionName, string columnName) {
    var dropped = false;
    ChangeSchema(() => dropped = _indexCatalog.Drop(collectionName, columnName));
    return dropped;
  }

  public bool RemoveRelation(string name) {
    var removed = false;
    ChangeSchema(() => {
      var relation = _relationCatalog.Descriptors.FirstOrDefault(candidate => candidate.Name == name);
      removed = _relationCatalog.Remove(name);
      if (removed && relation is not null) {
        RecordRelationChange(relation, RelationNodeKind.Removed);
      }
    });
    return removed;
  }

  //DC-4: the collection's primary index. The tree reads its own root out of the catalogue
  //document (D-2), so this hands back a view of what is on disk rather than a structure that
  //had to be built first.
  //The tree of a secondary index, for tests and diagnostics as PrimaryIndex is; null when the
  //column has none.
  public BPlusTree SecondaryIndex(string collectionName, string columnName) {
    return _indexCatalog.Find(collectionName, columnName)?.Tree;
  }

  public BPlusTree PrimaryIndex(string collectionName) {
    return _dataPageManager.PrimaryIndex(collectionName);
  }

  //Runs the action inside a transaction, so a caller driving the index directly gets the
  //same journal and the same rollback as everything else.
  //
  //A rollback undoes the pages, and the catalogue is read from pages — but it is also cached
  //in memory from open, so what the rollback took off the disk is still in the cache: a column
  //the failed action added, a page it allocated, an index root it moved. Everything read from
  //the catalogue is therefore read again, which is the same thing a reopen does and the only
  //way to be sure the cache and the file agree. It happens only when the outermost transaction
  //rolls back, because a nested one leaves the outer one to finish.
  public void InTransaction(Action action) {
    var transaction = _transactionManager.CreateTransaction();
    var outermost = transaction.IsOutermost;
    try {
      action();
      transaction.Commit();
    } catch {
      transaction.Rollback();
      if (outermost) {
        ReloadCatalogues();
      }
      throw;
    }
  }

  //The reload rebuilds every catalogue in memory, which is a schema change to anything planning
  //against them.
  private void ReloadCatalogues() {
    using var change = _catalogLock.Change();
    var transaction = _transactionManager.CreateTransaction();
    try {
      Initialize();
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public void CreateDatabase(Action<TokkDbConfiguration> configure) {
    ChangeSchema(() => {
      Initialize();
      var config = new TokkDbConfiguration();
      configure(config);
      foreach (var (name, entity) in config.Entities) {
        var descriptor = _catalog.CreateCollection(name, EntityColumns.Describe(entity.EntityType), entity.Description);
        CreateUniqueIndexes(descriptor);
        KeepVersionsByDefault(descriptor);
      }
    });
  }

  //QM-2b: a change to what a plan depends on — collections, their columns, indexes, relations.
  //It waits for every query holding a lease on the catalogue, no query starts while it runs, and
  //it moves the catalogue version, so a plan made before it is refused afterwards (QM-2a).
  //
  //The lease is taken before the transaction is, so a change that is waiting has touched nothing.
  private void ChangeSchema(Action action) {
    using var change = _catalogLock.Change();
    InTransaction(action);
  }

  public void Dispose() {
    _diskManager.Dispose();
    _catalogLock.Dispose();
  }

  //DC-4: a column declared unique is enforced by a unique index, and there is nowhere else
  //the enforcement could live — the check is a lookup by value, which is what an index is.
  private void CreateUniqueIndexes(CollectionDescriptor descriptor) {
    foreach (var column in descriptor.Columns.Where(column => column.Unique)) {
      //A column set that is being replaced mostly keeps the columns it had, so most of the
      //unique ones already have the index this would create.
      if (_indexCatalog.Find(descriptor.Name, column.Name) is null) {
        _indexCatalog.Create(descriptor.Name, column.Name, unique: true);
      }
    }
  }

  //Reading the root page first is what tells the rest of the engine the page size and where
  //the catalogue is; on a blank file it is what creates them.
  private void Initialize() {
    _rootPageManager.Initialize();
    _catalog.Initialize();
    //After the collections, because an index descriptor names the collection it covers, and
    //before anything is written, because a write maintains whatever is described here.
    _indexCatalog.Initialize();
    _relationCatalog.Initialize();
    //Neither structural: what a collection displays as and what the application notes about
    //it are their own documents (D-4), so a change to either leaves the schema alone.
    _settings.Initialize();
    //The free-space structures and the index trees hang off the catalogue, so they are stale
    //the moment it is reloaded and are read again from their roots on first use.
    _freeSpace.Reset();
    _dataPageManager.Reset();
    //Last, because it reads the history collections the catalogue has just described (HS-4).
    _versionStore.Initialize();
  }

}
