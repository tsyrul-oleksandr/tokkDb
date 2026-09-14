using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using TokkDb.Pages.Relations;
using TokkDb.Transactions;

namespace TokkDb.Pages.Versions;

//Layer 1 (§3). The one type that reads and writes history collections. Everything above it
//goes through IVersionStore (HS-3), and an architecture test over the compiled assemblies
//holds it to that. The members that are not on the contract — WriteNode, WriteOperation,
//VersionIndex — exist for the tests of layer 1 itself; no engine type may use them.
//
//A history collection is reserved, so it has no primary index and FindLiveRow on it would be
//a scan (§2.2, item 7). Every document in it is found through the version index (V-5): a node
//under (recordId, versionId), an operation under (operationId, operationId).
//
//The operations a later step implements throw until then, so that a caller wired up early
//fails loudly rather than silently doing nothing.
public sealed class VersionStore : IVersionStore {
  private readonly CollectionCatalog _catalog;
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;
  private readonly PageManager _pageManager;
  private readonly FreeSpaceManager _freeSpace;
  private readonly RelationCatalog _relations;
  private readonly CatalogLock _catalogLock;

  //A test hook (RH-7): called with each node a reconstruction examines, while it holds its
  //lease. Null in the engine; a test can block here and show a schema change waiting.
  public Action<VersionNode> ReconstructionProbe { get; set; }

  //RP-1's test hook: answers true for a node whose rerooting image the store is to drop, so
  //that a test can show the purge rolling that record back rather than committing a history
  //it cannot rebuild. Nothing in the engine sets it.
  public Func<VersionNode, bool> DropRerootedImageForTests { get; set; }

  //V-11 and HS-4: the schema and relation nodes of every history collection, by history
  //collection name, read at open and kept current by the recording operations. Few, needed by
  //every reconstruction, and never scanned for.
  private readonly Dictionary<string, SchemaHistory> _schemaHistories = new(StringComparer.Ordinal);

  //The index range that holds schema and relation nodes: under a sentinel "record" no real
  //record can have, because every real identifier carries a timestamp and this one is one.
  //They are read as a range, in logical-time order, never one by one (V-5).
  private static readonly Ulid SchemaHistoryKey = new([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);

  private sealed class SchemaHistory {
    public List<SchemaNode> Schemas { get; } = [];
    public List<RelationNode> Relations { get; } = [];
  }

  public VersionStore(CollectionCatalog catalog, DataPageManager dataPageManager,
      TransactionManager transactionManager, PageManager pageManager, FreeSpaceManager freeSpace,
      RelationCatalog relations, CatalogLock catalogLock) {
    _catalog = catalog;
    _dataPageManager = dataPageManager;
    _transactionManager = transactionManager;
    _pageManager = pageManager;
    _freeSpace = freeSpace;
    _relations = relations;
    _catalogLock = catalogLock;
  }

  //HS-4 and V-11. Reads every history collection's schema and relation nodes into memory: one
  //range of each version index, in logical-time order. Called at open and after a catalogue
  //reload, when what the recording operations added to memory may have been rolled back.
  public void Initialize() {
    _schemaHistories.Clear();
    foreach (var descriptor in _catalog.Descriptors.Where(descriptor => HistoryCollections.IsHistoryName(descriptor.Name)).ToList()) {
      _schemaHistories[descriptor.Name] = LoadSchemaHistory(descriptor.Name);
    }
  }

  private SchemaHistory LoadSchemaHistory(string historyName) {
    var history = new SchemaHistory();
    var encoded = KeyEncoder.Encode(SchemaHistoryKey);
    foreach (var entry in Index(historyName).Range(CompositeKey.ValuePrefix(encoded), CompositeKey.AboveValuePrefix(encoded))) {
      var record = ReadDocument(entry.Address);
      switch (HistoryDocuments.TypeOf(record.Document)) {
        case HistoryDocuments.SchemaType:
          history.Schemas.Add(HistoryDocuments.ReadSchemaNode(record));
          break;
        case HistoryDocuments.RelationType:
          history.Relations.Add(HistoryDocuments.ReadRelationNode(record));
          break;
      }
    }
    return history;
  }

  private SchemaHistory SchemaHistoryOf(string historyName) {
    if (!_schemaHistories.TryGetValue(historyName, out var history)) {
      history = LoadSchemaHistory(historyName);
      _schemaHistories[historyName] = history;
    }
    return history;
  }

  //V-4 and HS-2. In the caller's transaction, so that a failure between writing the policy and
  //creating the collection leaves neither. Reserved collections cannot be versioned (F-6): the
  //catalogue would have to be readable before its own history.
  public CollectionDescriptor CreateHistory(string collectionName) {
    _transactionManager.RequireTransaction();
    var owner = _catalog.Get(collectionName);
    if (owner.IsSystem) {
      throw new ReservedCollectionNameException(collectionName);
    }
    if (owner.HistoryCollectionId != default) {
      throw new InvalidOperationException(
        $"Collection '{collectionName}' already has a history collection.");
    }
    var history = _catalog.CreateHistoryCollection(HistoryCollections.NameFor(owner.Id), HistoryDocuments.Columns(),
      $"The version history of '{collectionName}' (V-4).");
    _catalog.SetHistoryCollectionId(collectionName, history.Id);
    _schemaHistories[history.Name] = new SchemaHistory();
    RecordSchemaAtSwitchOn(collectionName, owner);
    return history;
  }

  //HS-10 and V-11. What switch-on records: one schema node per migration step still in the
  //catalogue's log, marked as having unknown earlier declarations, the current schema, and one
  //relation node per relation naming the collection, marked as of unknown creation time.
  //Nothing was recording before this moment, and the nodes say so rather than pretend.
  private void RecordSchemaAtSwitchOn(string collectionName, CollectionDescriptor owner) {
    var steps = owner.Migrations.GroupBy(step => step.Version).OrderBy(group => group.Key).ToList();
    var currentRecorded = false;
    foreach (var group in steps) {
      var isCurrent = group.Key == owner.SchemaVersion;
      currentRecorded |= isCurrent;
      RecordSchema(collectionName, new SchemaNode {
        SchemaVersion = group.Key,
        Columns = isCurrent ? owner.Columns.ToList() : [],
        ColumnsKnown = isCurrent,
        Migrations = group.ToList(),
        UnknownEarlierDeclarations = true
      });
    }
    if (!currentRecorded) {
      RecordSchema(collectionName, new SchemaNode {
        SchemaVersion = owner.SchemaVersion,
        Columns = owner.Columns.ToList(),
        ColumnsKnown = true,
        Migrations = [],
        //A version above 1 with no step behind it: the log was cleared by a Rewrite, so what
        //came before is unknown. Version 1 has nothing before it.
        UnknownEarlierDeclarations = owner.SchemaVersion > 1
      });
    }
    foreach (var relation in _relations.Naming(collectionName)) {
      RecordRelation(collectionName, RelationNode.Created(relation, unknownCreationTime: true));
    }
  }

  //HS-10. In the transaction of the change, into memory and the history collection alike; a
  //rollback reloads memory from the collection. Nothing for a collection that keeps no versions.
  public SchemaNode RecordSchema(string collectionName, SchemaNode schema) {
    ArgumentNullException.ThrowIfNull(schema);
    _transactionManager.RequireTransaction();
    var owner = _catalog.Get(collectionName);
    if (owner.HistoryCollectionId == default) {
      return null;
    }
    var history = HistoryCollections.NameFor(owner.Id);
    var node = schema.WithId(RecordIdentity.Next());
    WriteSchemaHistoryDocument(history, node.Id, HistoryDocuments.WriteSchemaNode(node));
    SchemaHistoryOf(history).Schemas.Add(node);
    return node;
  }

  public RelationNode RecordRelation(string collectionName, RelationNode relation) {
    ArgumentNullException.ThrowIfNull(relation);
    _transactionManager.RequireTransaction();
    var owner = _catalog.Get(collectionName);
    if (owner.HistoryCollectionId == default) {
      return null;
    }
    var history = HistoryCollections.NameFor(owner.Id);
    var node = relation.WithId(RecordIdentity.Next());
    WriteSchemaHistoryDocument(history, node.Id, HistoryDocuments.WriteRelationNode(node));
    SchemaHistoryOf(history).Relations.Add(node);
    return node;
  }

  private void WriteSchemaHistoryDocument(string historyName, Ulid id, ObjectDocument document) {
    var header = RecordHeader.ForNewRecord(id, _catalog.Get(historyName).SchemaVersion);
    var row = _dataPageManager.WriteRecord(historyName, header, document);
    Index(historyName).Upsert(CompositeKey.Encode(SchemaHistoryKey, id), row.Address);
  }

  public IReadOnlyList<SchemaNode> SchemaNodes(string collectionName) {
    return SchemaHistoryOf(HistoryOf(collectionName)).Schemas;
  }

  public IReadOnlyList<RelationNode> RelationNodes(string collectionName) {
    return SchemaHistoryOf(HistoryOf(collectionName)).Relations;
  }

  //V-3 and V-11: the migration steps a record written under fromVersion is read through to be
  //at toVersion, from schema history — which Rewrite does not empty — in version order. What
  //was never recorded is not there: a step older than the switch-on is unknown.
  public IReadOnlyList<ColumnMigration> SchemaAt(string collectionName, ushort fromVersion, ushort toVersion) {
    return SchemaNodes(collectionName)
      .Where(node => node.SchemaVersion > fromVersion && node.SchemaVersion <= toVersion)
      .OrderBy(node => node.SchemaVersion)
      .SelectMany(node => node.Migrations.OrderBy(step => step.Version))
      .ToList();
  }

  //HS-2 and V-14. Every document is retired one by one rather than the catalogue entry being
  //dropped alone, because retiring is the path that secure release clears (RP-4), and a
  //collection dropped by its entry alone would leave every node's bytes on its pages. The
  //version index's pages are cleared and go back to the collection's pool the way a dropped
  //secondary index's do (BPlusTree.ReleasePages). Every live image's pointer into the dropped
  //history is zeroed, so that WV-10's first invariant — no history, a zero pointer — holds.
  public void DropHistory(string collectionName) {
    _transactionManager.RequireTransaction();
    var owner = _catalog.Get(collectionName);
    if (owner.HistoryCollectionId == default) {
      return;
    }
    var name = HistoryCollections.NameFor(owner.Id);
    if (_catalog.Exists(name)) {
      //V-17: the frame of this transaction would hold every node it retires.
      _transactionManager.RequireTransaction().MarkForFrameDiscard();
      foreach (var row in _dataPageManager.GetAllRows(name).ToArray()) {
        _dataPageManager.RetireRow(name, row.Address, RecordFlags.Deleted);
      }
      Index(name).ReleasePages();
      foreach (var row in _dataPageManager.GetAllRows(collectionName).ToArray()) {
        _dataPageManager.ZeroPreviousVersion(row.Address);
      }
      _catalog.SetVersionIndexRoot(name, default);
      _catalog.DropHistoryCollection(name);
      //The next history collection of this owner has this same name; nothing of the dropped
      //one's free space may be handed to it.
      _freeSpace.Forget(name);
    }
    _schemaHistories.Remove(name);
    _catalog.SetHistoryCollectionId(collectionName, default);
  }

  //The version index of a collection's history: one B+Tree over CompositeKey(recordId,
  //versionId), rooted in the history collection's catalogue document (V-5). Built on demand
  //from that root, so a catalogue reload needs no reset here.
  public BPlusTree VersionIndex(string collectionName) {
    return Index(HistoryOf(collectionName));
  }

  private BPlusTree Index(string historyName) {
    return new BPlusTree(_pageManager, _catalog, _freeSpace, _transactionManager, historyName,
      new VersionIndexRoot(_catalog, historyName));
  }

  private string HistoryOf(string collectionName) {
    var owner = _catalog.Get(collectionName);
    if (owner.HistoryCollectionId == default) {
      throw new InvalidOperationException($"Collection '{collectionName}' keeps no versions.");
    }
    return HistoryCollections.NameFor(owner.Id);
  }

  //(recordId, versionId): a record's versions sit together, in creation order (D-3, V-5).
  private static byte[] NodeKey(Ulid recordId, Ulid versionId) {
    return CompositeKey.Encode(recordId, versionId);
  }

  //(operationId, operationId): one descent, and never inside any record's range, because every
  //identifier comes from one source and an operation's can equal no record's (V-5).
  private static byte[] OperationKey(Ulid operationId) {
    return CompositeKey.Encode(operationId, operationId);
  }

  //V-7 and HS-6. The operation of the current outermost transaction in this collection's
  //history: minted with the first version the transaction records anywhere, and written once
  //into each history collection it touches, with the recorded time from the engine's clock and
  //the attribution the transaction was stamped with (HS-9).
  private Ulid EnsureOperation(string historyName) {
    var transaction = _transactionManager.RequireTransaction().Outermost;
    if (transaction.OperationId is null) {
      transaction.OperationId = RecordIdentity.Next();
      transaction.OperationRecordedAt = EngineClock.Now.UtcDateTime;
    }
    if (transaction.HistoriesWithOperation.Add(historyName)) {
      var attribution = transaction.Attribution ?? VersionAttribution.None;
      var header = RecordHeader.ForNewRecord(transaction.OperationId.Value, _catalog.Get(historyName).SchemaVersion);
      var row = _dataPageManager.WriteRecord(historyName, header, HistoryDocuments.WriteOperation(new Operation {
        Id = transaction.OperationId.Value,
        RecordedAt = transaction.OperationRecordedAt,
        Author = attribution.Author,
        Cause = attribution.Cause,
        Comment = attribution.Comment
      }));
      Index(historyName).Upsert(OperationKey(transaction.OperationId.Value), row.Address);
    }
    return transaction.OperationId.Value;
  }

  //WV-1. The Insert node of a new record: a root, a keyframe by definition, with no delta and
  //no image — the live image is the image while the record is at its first version.
  public DocumentAddress RecordInsert(string collectionName, RecordHeader head) {
    ArgumentNullException.ThrowIfNull(head);
    _transactionManager.RequireTransaction();
    var operationId = EnsureOperation(HistoryOf(collectionName));
    return WriteNode(collectionName, new VersionNode {
      RecordId = head.RecordId, VersionId = head.VersionId, Kind = VersionKind.Insert, Parent = null,
      OperationId = operationId, SchemaVersion = head.SchemaVersion, Distance = 0
    });
  }

  //WV-2 steps 2 and 4, WV-4 and V-1. The head's node first: a head that has none gets a
  //Baseline holding its stored image, flagged by its kind as having no recorded origin; a head
  //whose node is a keyframe without an image gets the image copied in, once, as it leaves the
  //head. Then the node for the change, whose parent is the head — or, for a restore, the
  //version restored. It is a keyframe, at distance 0 and with no delta, when its delta is at
  //least the large-delta ratio of the new image or when one more than its parent's distance
  //would reach or exceed the collection's interval; otherwise it stores the delta at that
  //distance. Each node keeps the bound of the interval in effect when it was written (HS-1).
  public DocumentAddress RecordSupersede(string collectionName, VersionKind kind, RecordHeader previousHead,
      ObjectDocument previousImage, RecordHeader newHead, ObjectDocument newImage, DocumentDelta delta,
      Ulid? restoredVersion) {
    ArgumentNullException.ThrowIfNull(previousHead);
    ArgumentNullException.ThrowIfNull(newHead);
    ArgumentNullException.ThrowIfNull(newImage);
    ArgumentNullException.ThrowIfNull(delta);
    _transactionManager.RequireTransaction();
    var owner = _catalog.Get(collectionName);
    var operationId = EnsureOperation(HistoryOf(collectionName));
    var headNode = HeadNodeWithImage(collectionName, previousHead, (ObjectDocumentValue)previousImage?.Value, operationId);
    var parent = restoredVersion is { } restored
      ? Node(collectionName, previousHead.RecordId, restored)
        ?? throw new InvalidOperationException($"Version {restored} of record {previousHead.RecordId} is not kept.")
      : headNode;
    var distance = DistanceOf(owner, parent, delta, newImage);
    return WriteNode(collectionName, new VersionNode {
      RecordId = newHead.RecordId, VersionId = newHead.VersionId, Kind = kind, Parent = parent.VersionId,
      OperationId = operationId, SchemaVersion = newHead.SchemaVersion, Distance = distance,
      Delta = distance == 0 ? null : delta,
      ReplacedHead = restoredVersion is null ? null : previousHead.VersionId
    });
  }

  //WV-3 and V-9. The head's node gets its image if it is a keyframe — or the head gets its
  //Baseline (WV-4) — and a Delete tombstone is added as the head's child. A tombstone holds
  //neither delta nor image and is never an ancestor: a restore of a deleted record is a child
  //of the version restored, not of the tombstone (RB-2). Its distance is one more than its
  //parent's, so it is never mistaken for a keyframe that should hold an image (WV-10).
  public DocumentAddress RecordDelete(string collectionName, RecordHeader head, ObjectDocument image) {
    ArgumentNullException.ThrowIfNull(head);
    ArgumentNullException.ThrowIfNull(image);
    _transactionManager.RequireTransaction();
    var operationId = EnsureOperation(HistoryOf(collectionName));
    var headNode = HeadNodeWithImage(collectionName, head, (ObjectDocumentValue)image.Value, operationId);
    return WriteNode(collectionName, new VersionNode {
      RecordId = head.RecordId, VersionId = RecordIdentity.NextAfter(head.VersionId), Kind = VersionKind.Delete,
      Parent = headNode.VersionId, OperationId = operationId, SchemaVersion = head.SchemaVersion,
      Distance = headNode.Distance + 1
    });
  }

  //WV-8. Rewrite is about to migrate the head's stored image and would lose something of it;
  //the image goes into the head's node first, and the head gets its Baseline when it has none.
  //Rewrite's transaction becomes an operation for the Baseline's sake, and creates no version.
  public DocumentAddress PreserveImage(string collectionName, RecordHeader head, ObjectDocument storedImage) {
    ArgumentNullException.ThrowIfNull(head);
    ArgumentNullException.ThrowIfNull(storedImage);
    _transactionManager.RequireTransaction();
    var operationId = EnsureOperation(HistoryOf(collectionName));
    var node = HeadNodeWithImage(collectionName, head, (ObjectDocumentValue)storedImage.Value, operationId);
    if (node.Image is null) {
      //A head that is not a keyframe keeps its image live only; storing it here is what
      //Rewrite asked for, and an image already held is never replaced (WV-8).
      node = RewriteNode(collectionName, node.WithImage((ObjectDocumentValue)storedImage.Value, head.SchemaVersion));
    }
    return node.Address;
  }

  //WV-2 step 2 and WV-4: the head's node as it has to be before the head leaves — with its
  //Baseline written if it had none, and with its image if it is a keyframe without one.
  private VersionNode HeadNodeWithImage(string collectionName, RecordHeader head, ObjectDocumentValue storedImage,
      Ulid operationId) {
    var headNode = HeadNode(collectionName, head);
    if (headNode is null) {
      if (storedImage is null) {
        throw new InvalidOperationException(
          $"Record {head.RecordId} has no node for its head {head.VersionId} and no image to write one from.");
      }
      var baseline = new VersionNode {
        RecordId = head.RecordId, VersionId = head.VersionId, Kind = VersionKind.Baseline, Parent = null,
        OperationId = operationId, SchemaVersion = head.SchemaVersion, Distance = 0, Image = storedImage,
        ImageSchemaVersion = head.SchemaVersion
      };
      return baseline.With(WriteNode(collectionName, baseline));
    }
    if (headNode.IsKeyframe && headNode.Image is null && storedImage is not null) {
      return RewriteNode(collectionName, headNode.WithImage(storedImage, head.SchemaVersion));
    }
    return headNode;
  }

  //V-1's two keyframe rules, on the serialized sizes and the collection's settings.
  private static int DistanceOf(CollectionDescriptor owner, VersionNode parent, DocumentDelta delta,
      ObjectDocument newImage) {
    var deltaBytes = CanonicalValue.Bytes(delta.ToDocumentValue()).Length;
    var imageBytes = CanonicalValue.Bytes(newImage.Value).Length;
    if (deltaBytes >= owner.LargeDeltaRatio * imageBytes) {
      return 0;
    }
    var distance = parent.Distance + 1;
    return distance >= owner.SnapshotInterval ? 0 : distance;
  }

  //A node written again with more in it — the image a keyframe gains as it leaves the head.
  //The old document is retired and the new one written, and the index entry follows it to
  //where it went; the node keeps its identity, which is what every other node refers to.
  private VersionNode RewriteNode(string collectionName, VersionNode node) {
    var history = HistoryOf(collectionName);
    _dataPageManager.RetireRow(history, node.Address, RecordFlags.Deleted);
    return node.With(WriteNode(collectionName, node));
  }

  //Writes a node's document into the history collection and its entry into the index. The
  //document's own record identifier is the version identifier (HS-5). Public for the tests of
  //this layer; the seam records versions through RecordInsert and RecordSupersede (step 3.2).
  public DocumentAddress WriteNode(string collectionName, VersionNode node) {
    ArgumentNullException.ThrowIfNull(node);
    _transactionManager.RequireTransaction();
    var history = HistoryOf(collectionName);
    var header = RecordHeader.ForNewRecord(node.VersionId, _catalog.Get(history).SchemaVersion);
    var row = _dataPageManager.WriteRecord(history, header, HistoryDocuments.WriteNode(node));
    Index(history).Upsert(NodeKey(node.RecordId, node.VersionId), row.Address);
    return row.Address;
  }

  public DocumentAddress WriteOperation(string collectionName, Operation operation) {
    ArgumentNullException.ThrowIfNull(operation);
    _transactionManager.RequireTransaction();
    var history = HistoryOf(collectionName);
    var header = RecordHeader.ForNewRecord(operation.Id, _catalog.Get(history).SchemaVersion);
    var row = _dataPageManager.WriteRecord(history, header, HistoryDocuments.WriteOperation(operation));
    Index(history).Upsert(OperationKey(operation.Id), row.Address);
    return row.Address;
  }

  //Null when the record has no such version — or when the key names an operation, which
  //shares the index and is no version of anything.
  public VersionNode Node(string collectionName, Ulid recordId, Ulid versionId) {
    var history = HistoryOf(collectionName);
    return Index(history).Find(NodeKey(recordId, versionId)) is { } address ? ReadNodeOrNull(recordId, address) : null;
  }

  //One range: the record's prefix to just above it (HS-4).
  public IReadOnlyList<VersionNode> Nodes(string collectionName, Ulid recordId) {
    var history = HistoryOf(collectionName);
    var encoded = KeyEncoder.Encode(recordId);
    return Index(history)
      .Range(CompositeKey.ValuePrefix(encoded), CompositeKey.AboveValuePrefix(encoded))
      .Select(entry => ReadNode(recordId, entry.Address))
      .ToList();
  }

  //The record's last version at or below an identifier: one floor search, then a check that
  //what it landed on is this record's, because the greatest key below the record's first
  //version belongs to something else (V-5).
  public VersionNode Floor(string collectionName, Ulid recordId, Ulid atOrBefore) {
    var history = HistoryOf(collectionName);
    if (Index(history).Floor(NodeKey(recordId, atOrBefore)) is not { } entry) {
      return null;
    }
    var prefix = CompositeKey.ValuePrefix(KeyEncoder.Encode(recordId));
    if (entry.Key.Length < prefix.Length || !entry.Key.AsSpan(0, prefix.Length).SequenceEqual(prefix)) {
      return null;
    }
    return ReadNodeOrNull(recordId, entry.Address);
  }

  public Operation Operation(string collectionName, Ulid operationId) {
    var history = HistoryOf(collectionName);
    if (Index(history).Find(OperationKey(operationId)) is not { } address) {
      return null;
    }
    var record = ReadDocument(address);
    return HistoryDocuments.TypeOf(record.Document) == HistoryDocuments.OperationType
      ? HistoryDocuments.ReadOperation(record)
      : null;
  }

  //HS-8 and V-6. A live image's PreviousVersion addresses the node of its own VersionId. The
  //pointer is checked before use, the way LiveRowAt checks an index entry: the slot has to
  //hold a live node document whose identifier is the head's version. When it does not — a
  //purge moved the node, or the pointer is zero — the index answers. A read never repairs it.
  public VersionNode HeadNode(string collectionName, RecordHeader head) {
    ArgumentNullException.ThrowIfNull(head);
    if (head.PreviousVersion != default) {
      try {
        if (_dataPageManager.LiveRowAt(head.PreviousVersion) is { } row) {
          var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
          if (record.Header.RecordId == head.VersionId && HistoryDocuments.TypeOf(record.Document) == HistoryDocuments.NodeType) {
            return HistoryDocuments.ReadNode(head.RecordId, record).With(row.Address);
          }
        }
      } catch (Exception exception) when (exception is not OutOfMemoryException) {
        //A pointer that leads to no readable page fails the check like one that leads to the
        //wrong document; the index below is the answer either way.
      }
    }
    return Node(collectionName, head.RecordId, head.VersionId);
  }

  //The node of the record's head: through the live image's pointer while there is a live
  //image, and otherwise the latest node, which is the tombstone (V-9).
  public VersionNode Head(string collectionName, Ulid recordId) {
    if (_dataPageManager.FindLiveRow(collectionName, recordId) is { } live) {
      return HeadNode(collectionName, StoredRecordUtilities.ReadHeader(live.Buffer));
    }
    return Floor(collectionName, recordId, Ulid.MaxValue);
  }

  private VersionNode ReadNode(Ulid recordId, DocumentAddress address) {
    return HistoryDocuments.ReadNode(recordId, ReadDocument(address)).With(address);
  }

  private VersionNode ReadNodeOrNull(Ulid recordId, DocumentAddress address) {
    var record = ReadDocument(address);
    return HistoryDocuments.TypeOf(record.Document) == HistoryDocuments.NodeType
      ? HistoryDocuments.ReadNode(recordId, record).With(address)
      : null;
  }

  private StoredRecord ReadDocument(DocumentAddress address) {
    var row = _dataPageManager.LiveRowAt(address)
      ?? throw new InvalidOperationException(
        $"The version index addresses page {address.PageIndex} slot {address.SlotIndex}, which holds no live document.");
    return StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
  }

  //RH-5, RH-7, RH-8 and V-3. The head reads its live image. Any other version walks its parents
  //to the nearest node holding an image — or to the head, whose image is live — and then comes
  //back down: at each node the image is migrated to that node's schema version through the
  //steps schema history holds (never the catalogue's log, which Rewrite empties), and the
  //node's delta is applied, with the version named on a mismatch (DL-7). The catalogue lease is
  //held throughout, so a schema change waits for the read to finish (QM-2b).
  public Reconstruction Reconstruct(string collectionName, Ulid recordId, Ulid versionId) {
    using var lease = _catalogLock.Read();
    var readsBefore = _pageManager.PageReadCount;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var target = Node(collectionName, recordId, versionId)
      ?? throw new VersionNotFoundException(collectionName, recordId, versionId);
    ReconstructionProbe?.Invoke(target);
    var examined = 1;
    if (target.Kind == VersionKind.Delete) {
      watch.Stop();
      return new Reconstruction {
        Node = target, IsDeleted = true, SchemaVersion = target.SchemaVersion,
        Report = new ReconstructionReport {
          VersionsExamined = examined, Keyframe = target.VersionId, DeltasApplied = 0,
          PagesRead = _pageManager.PageReadCount - readsBefore, Elapsed = watch.Elapsed
        }
      };
    }

    RecordHeader liveHeader = null;
    DataRow? live = _dataPageManager.FindLiveRow(collectionName, recordId);
    if (live is { } row) {
      liveHeader = StoredRecordUtilities.ReadHeader(row.Buffer);
    }

    //Up to the image. A node's own image comes first, even for the head: the only head whose
    //node holds one is a head Rewrite has since migrated, and the node's image is the version
    //as it was written (WV-8), which is what a reconstruction is for.
    var chain = new List<VersionNode> { target };
    var node = target;
    ObjectDocumentValue image;
    ushort imageSchema;
    while (true) {
      if (node.Image is not null) {
        image = node.Image;
        imageSchema = node.ImageSchemaVersion;
        break;
      }
      if (liveHeader is not null && node.VersionId == liveHeader.VersionId) {
        var stored = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(live!.Value));
        image = (ObjectDocumentValue)stored.Document.Value;
        imageSchema = liveHeader.SchemaVersion;
        break;
      }
      if (node.IsKeyframe) {
        throw new InvalidOperationException(
          $"Keyframe {node.VersionId} of record {recordId} has left the head and holds no image (WV-10).");
      }
      if (node.Parent is not { } parentId) {
        throw new InvalidOperationException(
          $"Version {node.VersionId} of record {recordId} is a root with no image (WV-10).");
      }
      node = Node(collectionName, recordId, parentId)
        ?? throw new VersionNotFoundException(collectionName, recordId, parentId);
      ReconstructionProbe?.Invoke(node);
      examined++;
      chain.Add(node);
    }
    var keyframe = node;

    //Down again, from the keyframe's child to the target.
    IDocumentValue current = image;
    var schema = imageSchema;
    var deltas = 0;
    for (var i = chain.Count - 2; i >= 0; i--) {
      var step = chain[i];
      if (step.SchemaVersion != schema) {
        current = SchemaMigrator.FromSteps(SchemaAt(collectionName, schema, step.SchemaVersion))
          .Apply(AsDocument(recordId, current)).Value;
        schema = step.SchemaVersion;
      }
      if (step.Delta is not null) {
        current = step.Delta.ApplyTo(current, step.VersionId);
        deltas++;
      }
    }
    watch.Stop();
    return new Reconstruction {
      Node = target, Document = AsDocument(recordId, current), SchemaVersion = schema,
      Report = new ReconstructionReport {
        VersionsExamined = examined, Keyframe = keyframe.VersionId, DeltasApplied = deltas,
        PagesRead = _pageManager.PageReadCount - readsBefore, Elapsed = watch.Elapsed
      }
    };
  }

  private static ObjectDocument AsDocument(Ulid recordId, IDocumentValue value) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(recordId));
    document.SetValue(value);
    return document;
  }

  //V-15 step 3. The node is written again as a root, the way a keyframe is written again when
  //it gains its image (RewriteNode): it keeps its identity, which every other node refers to,
  //and its address moves, which the head's pointer tolerates (HS-8). A tombstone keeps a
  //distance of 1 rather than 0, as WV-3 gave it, so that it is never taken for a keyframe that
  //should hold an image; every other rerooted node is a keyframe, with the image it was given
  //or already held — or with none when it is the live head, whose image is live.
  public void Reroot(string collectionName, Ulid recordId, Ulid versionId, ObjectDocument image, ushort imageSchemaVersion,
      Ulid cutFrom) {
    _transactionManager.RequireTransaction();
    var node = Node(collectionName, recordId, versionId)
      ?? throw new VersionNotFoundException(collectionName, recordId, versionId);
    var storedImage = node.Image;
    var storedSchema = node.ImageSchemaVersion;
    if (storedImage is null && image is not null && DropRerootedImageForTests?.Invoke(node) != true) {
      storedImage = (ObjectDocumentValue)image.Value;
      storedSchema = imageSchemaVersion;
    }
    RewriteNode(collectionName, new VersionNode {
      RecordId = recordId, VersionId = versionId, Kind = node.Kind, Parent = null, OperationId = node.OperationId,
      SchemaVersion = node.SchemaVersion, Distance = node.Kind == VersionKind.Delete ? 1 : 0, Delta = null,
      Image = storedImage, ImageSchemaVersion = storedImage is null ? (ushort)0 : storedSchema,
      ReplacedHead = node.ReplacedHead, CutFrom = cutFrom, Address = node.Address
    });
  }

  //V-15 step 4. A version already gone is skipped rather than refused: the purge that removes
  //it may have been interrupted after this record's commit and be running again (RP-1).
  public void Remove(string collectionName, Ulid recordId, IReadOnlyCollection<Ulid> versionIds) {
    ArgumentNullException.ThrowIfNull(versionIds);
    _transactionManager.RequireTransaction();
    var history = HistoryOf(collectionName);
    var index = Index(history);
    foreach (var versionId in versionIds) {
      var key = NodeKey(recordId, versionId);
      if (index.Find(key) is not { } address) {
        continue;
      }
      _dataPageManager.RetireRow(history, address, RecordFlags.Deleted);
      index.Delete(key);
    }
  }

  //RP-1 and I-7. One range read from just above the last record of the previous batch; it
  //stops as soon as it has the batch, so what it costs is the batch, not the history. An
  //operation's key names no record and the schema history's names none either (V-5).
  public IReadOnlyList<Ulid> NextRecords(string collectionName, Ulid? after, int limit) {
    ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
    var history = HistoryOf(collectionName);
    var from = after is { } last ? CompositeKey.AboveValuePrefix(KeyEncoder.Encode(last)) : null;
    var records = new List<Ulid>(limit);
    Ulid? current = null;
    foreach (var entry in Index(history).Range(from, null)) {
      var recordId = DecodeRecordId(entry.Key);
      if (recordId == current || recordId == SchemaHistoryKey || recordId == CompositeKey.ReadRecordId(entry.Key)) {
        continue;
      }
      current = recordId;
      records.Add(recordId);
      if (records.Count == limit) {
        break;
      }
    }
    return records;
  }

  //V-15. The one scan a purge makes: every operation document in the history, and every
  //operation a node still names; what is in the first set and not the second is retired. A
  //scan, because nothing but a scan can know what no node references — which is why a
  //per-record purge and an erase leave operations to the pass that ends a collection-wide
  //purge (V-12).
  public int RemoveUnreferencedOperations(string collectionName) {
    _transactionManager.RequireTransaction();
    var history = HistoryOf(collectionName);
    var index = Index(history);
    var operations = new Dictionary<Ulid, DocumentAddress>();
    var referenced = new HashSet<Ulid>();
    foreach (var entry in index.Scan()) {
      var versionId = CompositeKey.ReadRecordId(entry.Key);
      var recordId = DecodeRecordId(entry.Key);
      if (recordId == SchemaHistoryKey) {
        continue;
      }
      if (recordId == versionId) {
        operations[versionId] = entry.Address;
        continue;
      }
      referenced.Add(ReadNode(recordId, entry.Address).OperationId);
    }
    var removed = 0;
    foreach (var (operationId, address) in operations) {
      if (referenced.Contains(operationId)) {
        continue;
      }
      _dataPageManager.RetireRow(history, address, RecordFlags.Deleted);
      index.Delete(OperationKey(operationId));
      removed++;
    }
    return removed;
  }

  //RP-5 and V-17. Every node of the record, tombstone included, retired with its index entry;
  //the operation documents stay, holding no record values (V-12), for the collection's next
  //purge. The transaction is marked to discard its frame, whose before images are the nodes.
  public void EraseRecord(string collectionName, Ulid recordId) {
    var transaction = _transactionManager.RequireTransaction();
    Remove(collectionName, recordId, Nodes(collectionName, recordId).Select(node => node.VersionId).ToList());
    transaction.MarkForFrameDiscard();
  }

  //WV-10. The four invariants, checked by scanning the history collection, its index and the
  //collection's live images — the one place a scan of a history collection is the point. The
  //first problem's node is named (RP-8).
  public HistoryVerification Verify(string collectionName) {
    var owner = _catalog.Get(collectionName);
    if (owner.HistoryCollectionId == default) {
      return new HistoryVerification();
    }
    var history = HistoryOf(collectionName);
    var problems = new List<string>();
    Ulid? failingRecord = null;
    Ulid? failingVersion = null;
    void Problem(string text, Ulid? recordId, Ulid? versionId) {
      problems.Add(text);
      if (failingRecord is null && failingVersion is null) {
        failingRecord = recordId;
        failingVersion = versionId;
      }
    }

    //Invariant 2, one way: every index entry addresses a live document of the kind its key says.
    var entries = 0;
    var nodesByVersion = new Dictionary<Ulid, VersionNode>();
    var byRecord = new Dictionary<Ulid, List<VersionNode>>();
    foreach (var entry in Index(history).Scan()) {
      entries++;
      var versionId = CompositeKey.ReadRecordId(entry.Key);
      var recordId = DecodeRecordId(entry.Key);
      if (recordId == versionId || recordId == SchemaHistoryKey) {
        //An operation, a schema node or a relation node: checked for presence only.
        if (_dataPageManager.LiveRowAt(entry.Address) is null) {
          Problem($"index entry for document {versionId} addresses no live document", null, null);
        }
        continue;
      }
      if (_dataPageManager.LiveRowAt(entry.Address) is not { } row) {
        Problem($"index entry for version {versionId} of record {recordId} addresses no live document", recordId, versionId);
        continue;
      }
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (record.Header.RecordId != versionId || HistoryDocuments.TypeOf(record.Document) != HistoryDocuments.NodeType) {
        Problem($"index entry for version {versionId} of record {recordId} addresses document {record.Header.RecordId}", recordId, versionId);
        continue;
      }
      if (nodesByVersion.ContainsKey(versionId)) {
        Problem($"version {versionId} has more than one index entry", recordId, versionId);
        continue;
      }
      var node = HistoryDocuments.ReadNode(recordId, record).With(entry.Address);
      nodesByVersion[versionId] = node;
      if (!byRecord.TryGetValue(recordId, out var nodes)) {
        byRecord[recordId] = nodes = [];
      }
      nodes.Add(node);
    }

    //Invariant 2, the other way: every node document has an entry.
    var documents = 0;
    foreach (var row in _dataPageManager.GetAllRows(history)) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (!record.Header.IsLive || HistoryDocuments.TypeOf(record.Document) != HistoryDocuments.NodeType) {
        continue;
      }
      documents++;
      if (!nodesByVersion.TryGetValue(record.Header.RecordId, out var node) || node.Address != row.Address) {
        //The document carries its version; which record it belongs to is what the missing entry said.
        Problem($"node {record.Header.RecordId} has no index entry addressing it", null, record.Header.RecordId);
      }
    }

    //Invariants 1, 3 and 4, per record.
    var seen = new HashSet<Ulid>();
    foreach (var row in _dataPageManager.GetAllRows(collectionName)) {
      var header = StoredRecordUtilities.ReadHeader(row.Buffer);
      if (!header.IsLive) {
        continue;
      }
      seen.Add(header.RecordId);
      if (!byRecord.TryGetValue(header.RecordId, out var nodes)) {
        if (header.PreviousVersion != default) {
          Problem($"live record {header.RecordId} has no history but a pointer to page {header.PreviousVersion.PageIndex}", header.RecordId, header.VersionId);
        }
        continue;
      }
      var head = nodes.MaxBy(node => node.VersionId);
      if (head!.VersionId != header.VersionId) {
        Problem(nodesByVersion.ContainsKey(header.VersionId)
          ? $"record {header.RecordId} has two heads: the live image {header.VersionId} and node {head.VersionId}"
          : $"live record {header.RecordId} at version {header.VersionId} has no head node", header.RecordId, head.VersionId);
      }
      //The pointer is not an invariant: a purge that reroots the head moves its node and leaves
      //the pointer behind, which HS-8 allows — it is checked before use and the index answers.
      CheckImages(nodes, head.VersionId, Problem);
    }
    foreach (var (recordId, nodes) in byRecord) {
      if (seen.Contains(recordId)) {
        continue;
      }
      var head = nodes.MaxBy(node => node.VersionId);
      if (head!.Kind != VersionKind.Delete) {
        Problem($"record {recordId} has no live image and its last node {head.VersionId} is not a tombstone", recordId, head.VersionId);
      }
      CheckImages(nodes, head.VersionId, Problem);
    }

    return new HistoryVerification {
      Problems = problems, Records = seen.Union(byRecord.Keys).Count(), Nodes = documents, IndexEntries = entries,
      FailingRecord = failingRecord, FailingVersion = failingVersion
    };
  }

  //RP-7. One scan of the index, reading each node once, and one of the history collection's
  //rows for the page count.
  public HistoryReport Report(string collectionName) {
    var owner = _catalog.Get(collectionName);
    if (owner.HistoryCollectionId == default) {
      return new HistoryReport { CollectionName = collectionName };
    }
    var history = HistoryOf(collectionName);
    var records = new HashSet<Ulid>();
    int nodes = 0, keyframes = 0, operations = 0;
    long deltaBytes = 0, imageBytes = 0;
    ushort? oldest = null;
    void Refer(ushort schemaVersion) {
      oldest = oldest is { } known && known <= schemaVersion ? known : schemaVersion;
    }
    foreach (var entry in Index(history).Scan()) {
      var versionId = CompositeKey.ReadRecordId(entry.Key);
      var recordId = DecodeRecordId(entry.Key);
      if (recordId == SchemaHistoryKey) {
        continue;
      }
      if (recordId == versionId) {
        operations++;
        continue;
      }
      var node = ReadNode(recordId, entry.Address);
      records.Add(recordId);
      nodes++;
      if (node.IsKeyframe) {
        keyframes++;
      }
      if (node.Delta is not null) {
        deltaBytes += CanonicalValue.Bytes(node.Delta.ToDocumentValue()).Length;
      }
      if (node.Image is not null) {
        imageBytes += CanonicalValue.Bytes(node.Image).Length;
        Refer(node.ImageSchemaVersion);
      }
      Refer(node.SchemaVersion);
    }
    var dataPages = _dataPageManager.GetAllRows(history).Select(row => row.Address.PageIndex).Distinct().Count();
    return new HistoryReport {
      CollectionName = collectionName, Records = records.Count, Nodes = nodes, Keyframes = keyframes, Operations = operations,
      DeltaBytes = deltaBytes, ImageBytes = imageBytes, DataPages = dataPages, IndexPages = Index(history).Nodes().Count(),
      OldestSchemaVersion = oldest
    };
  }

  //Invariant 4: every node that is not a head, and whose distance is 0, holds an image.
  private static void CheckImages(List<VersionNode> nodes, Ulid head, Action<string, Ulid?, Ulid?> problem) {
    foreach (var node in nodes) {
      if (node.VersionId != head && node.Distance == 0 && node.Image is null) {
        problem($"keyframe {node.VersionId} of record {node.RecordId} has left the head and holds no image", node.RecordId, node.VersionId);
      }
    }
  }

  //The record half of a version index key: the value prefix CompositeKey terminated, with its
  //escapes undone.
  private static Ulid DecodeRecordId(byte[] key) {
    var prefix = key.AsSpan(0, key.Length - KeyEncoder.UlidKeyByteSize - 2);
    var bytes = new List<byte>(KeyEncoder.UlidKeyByteSize);
    for (var i = 0; i < prefix.Length; i++) {
      bytes.Add(prefix[i]);
      if (prefix[i] == 0) {
        i++;
      }
    }
    return KeyEncoder.DecodeUlid(bytes.ToArray());
  }
}
