using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Transactions;

namespace TokkDb;

//Layer 4's purge (RP-1 to RP-3): V-15's four steps over one record at a time, each in its own
//transaction, through the store's contract only. A collection-wide purge takes its records in
//batches (I-7) and ends with the operation pass; a per-record purge is one record and no pass.
internal sealed class HistoryPurge {
  private readonly IVersionStore _store;
  private readonly DataPageManager _dataPageManager;
  private readonly TransactionManager _transactionManager;

  public HistoryPurge(IVersionStore store, DataPageManager dataPageManager, TransactionManager transactionManager) {
    _store = store;
    _dataPageManager = dataPageManager;
    _transactionManager = transactionManager;
  }

  public PurgeReport PurgeCollection(string collectionName, DateTimeOffset before, int batchSize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
    RequireNoTransaction();
    var tally = new Tally(collectionName, before);
    Ulid? after = null;
    while (true) {
      var batch = _store.NextRecords(collectionName, after, batchSize);
      foreach (var recordId in batch) {
        PurgeRecord(collectionName, recordId, before, tally);
      }
      if (batch.Count < batchSize) {
        break;
      }
      after = batch[^1];
    }
    //The pass that ends a collection-wide purge (V-15), a transaction of its own.
    InTransaction(() => tally.OperationsRemoved = _store.RemoveUnreferencedOperations(collectionName));
    return tally.Report();
  }

  public PurgeReport PurgeRecord(string collectionName, Ulid recordId, DateTimeOffset before) {
    RequireNoTransaction();
    var tally = new Tally(collectionName, before);
    PurgeRecord(collectionName, recordId, before, tally);
    return tally.Report();
  }

  //V-15 for one record, in one transaction.
  private void PurgeRecord(string collectionName, Ulid recordId, DateTimeOffset before, Tally tally) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      //V-17: a purge transaction's before images are the nodes it removes.
      transaction.MarkForFrameDiscard();
      tally.RecordsExamined++;
      var nodes = _store.Nodes(collectionName, recordId);
      if (nodes.Count == 0) {
        transaction.Commit();
        return;
      }

      //Steps 1 and 2. H is the head at T by logical time — the last version at or before the
      //moment, exactly as GetAsOf(T) finds it — and K is every node created after T, plus H.
      //
      //Why K is enough (V-15's proof, which is what the tests of this class cover):
      //(a) for every t ≥ T, GetAsOf(t) answers the same before and after: the head at t was
      //    either created at or after T, and is in K by the first rule, or was created before T
      //    and stayed head from then until t, so it was head at T too, and is H;
      //(b) every kept version reconstructs to the same document, and (c) reaches an image within
      //    its stored distance: take a kept version m and walk its parents inside K. The walk
      //    ends either at an ancestor that already had an image — kept, and unchanged — or at a
      //    node whose parent is not in K, which step 3 below gives an image computed from the
      //    tree as it is before anything is removed. Every node on the walk is kept and
      //    unchanged, so the deltas applied are the same ones; and that node is m's old image
      //    ancestor or lies between it and m, so the actual distance can only have shrunk.
      var cutoff = LogicalTime.AtOrBefore(before);
      var headAtMoment = nodes.Where(node => node.VersionId.CompareTo(cutoff) <= 0).MaxBy(node => node.VersionId);
      var kept = nodes
        .Where(node => node.VersionId.CompareTo(cutoff) > 0 || node.VersionId == headAtMoment?.VersionId)
        .ToList();
      var keptIds = kept.Select(node => node.VersionId).ToHashSet();
      var removed = nodes.Where(node => !keptIds.Contains(node.VersionId)).Select(node => node.VersionId).ToList();
      if (removed.Count == 0) {
        transaction.Commit();
        return;
      }
      var liveHead = LiveHead(collectionName, recordId);

      //Step 3, before anything is removed: every kept node whose parent is not kept becomes a
      //root with the image it needs. The live head's image is live and a tombstone has none; a
      //keyframe that already holds its image keeps it.
      var rerooted = 0;
      foreach (var node in kept) {
        if (node.Parent is not { } parent || keptIds.Contains(parent)) {
          continue;
        }
        if (node.VersionId == liveHead || node.Kind == VersionKind.Delete || node.Image is not null) {
          _store.Reroot(collectionName, recordId, node.VersionId, null, 0, parent);
        } else {
          var reconstruction = _store.Reconstruct(collectionName, recordId, node.VersionId);
          _store.Reroot(collectionName, recordId, node.VersionId, reconstruction.Document, reconstruction.SchemaVersion, parent);
        }
        rerooted++;
      }

      //Step 4.
      _store.Remove(collectionName, recordId, removed);

      //RP-1: the kept forest is checked before it is committed, and rolled back rather than
      //committed when it could not be rebuilt.
      if (CheckKeptForest(collectionName, recordId, liveHead) is { } problem) {
        transaction.Rollback();
        _transactionManager.AfterOutermostRollback?.Invoke();
        tally.Failures.Add(new PurgeFailure(recordId, problem));
        return;
      }
      transaction.Commit();
      tally.RecordsChanged++;
      tally.NodesRemoved += removed.Count;
      tally.NodesRerooted += rerooted;
    } catch {
      if (transaction.State == Pages.Transactions.TransactionState.Active) {
        transaction.Rollback();
        _transactionManager.AfterOutermostRollback?.Invoke();
      }
      throw;
    }
  }

  //WV-10's invariants for this record, and V-15's: every kept node's parent is kept, or the
  //node is a root holding an image, the live head, or a tombstone — which V-15 keeps as a
  //parentless root when nothing after T remains, answering "deleted" and needing no image; and
  //every kept version reconstructs, reaching an image within its stored distance.
  private string CheckKeptForest(string collectionName, Ulid recordId, Ulid? liveHead) {
    IReadOnlyList<VersionNode> kept;
    try {
      kept = _store.Nodes(collectionName, recordId);
    } catch (Exception exception) when (exception is not OutOfMemoryException) {
      return $"the kept nodes cannot be read: {exception.Message}";
    }
    if (kept.Count == 0) {
      return "no node is left";
    }
    var keptIds = kept.Select(node => node.VersionId).ToHashSet();
    var head = kept.MaxBy(node => node.VersionId)!;
    if (liveHead is { } live ? head.VersionId != live : head.Kind != VersionKind.Delete) {
      return liveHead is null
        ? $"the record has no live image and its last kept node {head.VersionId} is not a tombstone"
        : $"the live image is at version {liveHead} and the last kept node is {head.VersionId}";
    }
    foreach (var node in kept) {
      if (node.Parent is { } parent) {
        if (!keptIds.Contains(parent)) {
          return $"kept version {node.VersionId} has parent {parent}, which is not kept";
        }
      } else if (node.Image is null && node.VersionId != liveHead && node.Kind != VersionKind.Delete) {
        return $"root {node.VersionId} holds no image and is not the live head";
      }
      if (node.IsKeyframe && node.Image is null && node.VersionId != head.VersionId) {
        return $"keyframe {node.VersionId} has left the head and holds no image (WV-10)";
      }
      if (node.Kind == VersionKind.Delete) {
        continue;
      }
      Reconstruction reconstruction;
      try {
        reconstruction = _store.Reconstruct(collectionName, recordId, node.VersionId);
      } catch (Exception exception) when (exception is not OutOfMemoryException) {
        return $"version {node.VersionId} does not reconstruct: {exception.Message}";
      }
      var stepsUp = reconstruction.Report.VersionsExamined - 1;
      if (stepsUp > node.Distance) {
        return $"version {node.VersionId} reaches an image {stepsUp} steps up, beyond its stored distance {node.Distance}";
      }
    }
    return null;
  }

  private Ulid? LiveHead(string collectionName, Ulid recordId) {
    return _dataPageManager.FindLiveRow(collectionName, recordId) is { } row
      ? StoredRecordUtilities.ReadHeader(row.Buffer).VersionId
      : null;
  }

  private void InTransaction(Action action) {
    var transaction = _transactionManager.CreateTransaction();
    try {
      transaction.MarkForFrameDiscard();
      action();
      transaction.Commit();
    } catch {
      transaction.Rollback();
      _transactionManager.AfterOutermostRollback?.Invoke();
      throw;
    }
  }

  //One record per transaction means the purge's transactions are outermost: inside a unit of
  //work they would be nested, and a record rolled back would take the unit of work with it.
  private void RequireNoTransaction() {
    if (_transactionManager.Current is not null) {
      throw new InvalidOperationException(
        "PurgeHistory runs one transaction per record and cannot run inside an open transaction.");
    }
  }

  private sealed class Tally(string collectionName, DateTimeOffset before) {
    public int RecordsExamined;
    public int RecordsChanged;
    public int NodesRemoved;
    public int NodesRerooted;
    public int OperationsRemoved;
    public readonly List<PurgeFailure> Failures = [];

    public PurgeReport Report() {
      return new PurgeReport {
        CollectionName = collectionName, Before = before, RecordsExamined = RecordsExamined, RecordsChanged = RecordsChanged,
        NodesRemoved = NodesRemoved, NodesRerooted = NodesRerooted, OperationsRemoved = OperationsRemoved, Failures = Failures
      };
    }
  }
}
