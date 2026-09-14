using System.Diagnostics;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests;

//HS-4, HS-5, HS-6 and HS-8: nodes and operations live in the history collection and are
//found through the version index, never by a scan of the collection.
public class VersionStoreTests(ITestOutputHelper output) {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewVersioned(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    return db;
  }

  private static VersionStore Store(TokkDbConnection db) {
    return (VersionStore)db.Versions;
  }

  private static VersionNode Node(Ulid recordId, Ulid versionId, Ulid? parent, Ulid operationId, int distance,
      VersionKind kind = VersionKind.Update) {
    return new VersionNode {
      RecordId = recordId, VersionId = versionId, Parent = parent, OperationId = operationId,
      SchemaVersion = 1, Distance = distance, Kind = kind
    };
  }

  [Fact]
  public void EveryFieldOfANodeAndAnOperationRoundTrips() {
    using var file = new TempDatabaseFile();
    var recordId = RecordIdentity.Next();
    var first = RecordIdentity.Next();
    var second = RecordIdentity.Next();
    var operationId = RecordIdentity.Next();
    var replaced = RecordIdentity.Next();
    var cutFrom = RecordIdentity.Next();
    var cause = RecordIdentity.Next();
    var delta = DocumentDiff.Compute(Obj(("a", Int(1))), Obj(("a", Int(2)), ("b", Str("x"))));
    var image = Obj(("name", Str("Ivan")), ("age", Int(29)));
    var recordedAt = new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Utc);
    string historyName;
    using (var db = NewVersioned(file)) {
      historyName = HistoryCollections.NameFor(db.Collection(Collection).Id);
      var store = Store(db);
      db.InTransaction(() => {
        store.WriteNode(Collection, new VersionNode {
          RecordId = recordId, VersionId = first, Kind = VersionKind.Insert, Parent = null, OperationId = operationId,
          SchemaVersion = 1, Distance = 0, Image = image, ImageSchemaVersion = 1
        });
        store.WriteNode(Collection, new VersionNode {
          RecordId = recordId, VersionId = second, Kind = VersionKind.Restore, Parent = first, OperationId = operationId,
          SchemaVersion = 2, Distance = 1, Delta = delta, ReplacedHead = replaced, CutFrom = cutFrom
        });
        store.WriteOperation(Collection, new Operation {
          Id = operationId, RecordedAt = recordedAt, Author = "olexander", Cause = cause, Comment = "a comment"
        });
      });
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var read = Store(reopened);

    var root = read.Node(Collection, recordId, first);
    Assert.NotNull(root);
    Assert.Equal(recordId, root.RecordId);
    Assert.Equal(first, root.VersionId);
    Assert.Equal(VersionKind.Insert, root.Kind);
    Assert.Null(root.Parent);
    Assert.True(root.IsRoot);
    Assert.True(root.IsKeyframe);
    Assert.Equal(operationId, root.OperationId);
    Assert.Equal(1, root.SchemaVersion);
    Assert.Equal(0, root.Distance);
    Assert.Null(root.Delta);
    Assert.True(CanonicalValue.Equal(image, root.Image));
    Assert.Equal(1, root.ImageSchemaVersion);
    Assert.Null(root.ReplacedHead);
    Assert.Null(root.CutFrom);
    Assert.NotEqual(default, root.Address);
    Assert.Equal(first.Time, root.LogicalTime);

    var restore = read.Node(Collection, recordId, second);
    Assert.NotNull(restore);
    Assert.Equal(VersionKind.Restore, restore.Kind);
    Assert.Equal(first, restore.Parent);
    Assert.Equal(2, restore.SchemaVersion);
    Assert.Equal(1, restore.Distance);
    Assert.False(restore.IsKeyframe);
    Assert.NotNull(restore.Delta);
    Assert.Equal(delta.Elements.Count, restore.Delta.Elements.Count);
    Assert.Equal(delta.Elements[0].Path, restore.Delta.Elements[0].Path);
    Assert.Null(restore.Image);
    Assert.Equal(replaced, restore.ReplacedHead);
    Assert.Equal(cutFrom, restore.CutFrom);

    Assert.Equal([first, second], read.Nodes(Collection, recordId).Select(node => node.VersionId));
    Assert.Equal(second, read.Floor(Collection, recordId, Ulid.MaxValue).VersionId);
    Assert.Equal(second, read.Head(Collection, recordId).VersionId);
    Assert.Null(read.Node(Collection, recordId, RecordIdentity.Next()));

    var operation = read.Operation(Collection, operationId);
    Assert.NotNull(operation);
    Assert.Equal(recordedAt, operation.RecordedAt);
    Assert.Equal("olexander", operation.Author);
    Assert.Equal(cause, operation.Cause);
    Assert.Equal("a comment", operation.Comment);
    Assert.Equal(operationId.Time, operation.LogicalTime);
    Assert.Null(read.Operation(Collection, recordId));

    //HS-5: the shape is declared in the catalogue.
    var columns = reopened.Collection(historyName).Columns.Select(column => column.Name).ToList();
    foreach (var field in new[] {
      HistoryDocuments.TypeField, HistoryDocuments.KindField, HistoryDocuments.ParentField, HistoryDocuments.OperationField,
      HistoryDocuments.SchemaVersionField, HistoryDocuments.DistanceField, HistoryDocuments.DeltaField,
      HistoryDocuments.ImageField, HistoryDocuments.ImageSchemaVersionField, HistoryDocuments.ReplacedHeadField,
      HistoryDocuments.CutFromField, HistoryDocuments.RecordedAtField, HistoryDocuments.AuthorField,
      HistoryDocuments.CauseField, HistoryDocuments.CommentField
    }) {
      Assert.Contains(field, columns);
    }
    Assert.NotEqual(0u, reopened.Collection(historyName).VersionIndexRoot);
  }

  private static byte[] Key(long value) {
    return KeyEncoder.Encode(value).Bytes;
  }

  //HS-4: a property test of Floor against a linear search, over a tree with gaps and deletions.
  [Fact]
  public void FloorAgreesWithALinearSearch() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var tree = db.PrimaryIndex(Collection);
    var random = new Random(20260914);
    var values = Enumerable.Range(0, 6_000).Select(_ => (long)random.Next(-5_000, 5_000)).Distinct().ToList();
    db.InTransaction(() => {
      foreach (var value in values.OrderBy(_ => random.Next())) {
        tree.Insert(Key(value), new DocumentAddress((uint)(value + 6_000), 1));
      }
    });
    var deleted = values.OrderBy(_ => random.Next()).Take(values.Count / 3).ToList();
    db.InTransaction(() => {
      foreach (var value in deleted) {
        Assert.True(tree.Delete(Key(value)));
      }
    });
    var remaining = values.Except(deleted).Order().ToList();

    Assert.Null(tree.Floor(Key(remaining[0] - 1)));
    Assert.Null(tree.Floor(Key(long.MinValue)));
    for (var probe = 0; probe < 3_000; probe++) {
      var key = (long)random.Next(-6_000, 6_000);
      var expected = remaining.Where(value => value <= key).Select(value => (long?)value).LastOrDefault();
      var floor = tree.Floor(Key(key));
      if (expected is null) {
        Assert.Null(floor);
      } else {
        Assert.NotNull(floor);
        Assert.Equal(Key(expected.Value), floor.Value.Key);
      }
    }
  }

  //HS-4's bounds. Its numbers are 10 000 records of 100 versions each, which the engine writes
  //in about four minutes — too long for every run — so the suite runs 10 000 records of 10
  //versions, which already gives a tree of the height the bounds are about, and the full
  //count runs when TOKKDB_VERSION_INDEX_FULL is set. The bounds are in terms of the height
  //either way.
  [Fact]
  public void FloorListingAndOperationLookupsReadWithinTheTreesHeight() {
    const int records = 10_000;
    var versions = Environment.GetEnvironmentVariable("TOKKDB_VERSION_INDEX_FULL") is null ? 10 : 100;
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var store = Store(db);
    //About ten thousand nodes per transaction, whichever count runs: what a commit costs is
    //paid per transaction, and a hundred small ones would spend the test's time there.
    var recordsPerBatch = 10_000 / versions;
    var batches = records / recordsPerBatch;
    var recordIds = new Ulid[records];
    var versionIds = new Ulid[records][];
    var operationIds = new Ulid[batches];
    var watch = Stopwatch.StartNew();
    for (var batch = 0; batch < batches; batch++) {
      var operationId = RecordIdentity.Next();
      operationIds[batch] = operationId;
      db.InTransaction(() => {
        store.WriteOperation(Collection, new Operation { Id = operationId, RecordedAt = DateTime.UtcNow });
        for (var r = batch * recordsPerBatch; r < (batch + 1) * recordsPerBatch; r++) {
          recordIds[r] = RecordIdentity.Next();
          versionIds[r] = new Ulid[versions];
          Ulid? parent = null;
          for (var v = 0; v < versions; v++) {
            versionIds[r][v] = RecordIdentity.Next();
            store.WriteNode(Collection, Node(recordIds[r], versionIds[r][v], parent, operationId, v % 8,
              v == 0 ? VersionKind.Insert : VersionKind.Update));
            parent = versionIds[r][v];
          }
        }
      });
    }
    watch.Stop();
    var tree = store.VersionIndex(Collection);
    var height = tree.Height();
    output.WriteLine($"{records:N0} records x {versions} versions written in {watch.Elapsed.TotalSeconds:F1} s; tree height {height}, {file.PageCount:N0} pages");
    Assert.True(height >= 2);

    long Reads(Action action) {
      var before = db.PageReadCount;
      action();
      return db.PageReadCount - before;
    }

    //Floor, whichever version it lands on.
    foreach (var r in new[] { 0, 1, 4_999, 7_777, records - 1 }) {
      foreach (var v in new[] { 0, 1, versions / 2, versions - 1 }) {
        var key = CompositeKey.Encode(recordIds[r], versionIds[r][v]);
        IndexEntry? entry = null;
        var reads = Reads(() => entry = tree.Floor(key));
        Assert.True(reads <= 2 * height, $"Floor read {reads} pages at height {height}");
        Assert.Equal(key, entry!.Value.Key);
        VersionNode node = null;
        reads = Reads(() => node = store.Floor(Collection, recordIds[r], versionIds[r][v]));
        Assert.True(reads <= 2 * height + 1, $"the store's Floor read {reads} pages at height {height}");
        Assert.Equal(versionIds[r][v], node!.VersionId);
      }
      //Above the last version: the last version. Below the first: nothing of this record.
      Assert.Equal(versionIds[r][^1], store.Floor(Collection, recordIds[r], Ulid.MaxValue).VersionId);
      Assert.Null(store.Floor(Collection, recordIds[r], Ulid.MinValue));
    }

    //The answer in the previous leaf: a leaf whose first key has been deleted keeps a
    //separator below every key it still holds, so a search for that key lands in it and has
    //to step to the leaf before.
    var leaves = tree.Leaves().Take(3).ToList();
    var target = leaves[2];
    var previous = leaves[1];
    var deletedKey = target.Entries[0].Key;
    var expectedKey = previous.Entries[^1].Key;
    db.InTransaction(() => Assert.True(tree.Delete(deletedKey)));
    IndexEntry? stepped = null;
    var steppedReads = Reads(() => stepped = tree.Floor(deletedKey));
    Assert.Equal(expectedKey, stepped!.Value.Key);
    Assert.True(steppedReads <= 2 * height, $"Floor into the previous leaf read {steppedReads} pages at height {height}");

    //Listing one record reads its range: a descent and the leaves it spans, plus one page
    //per node document.
    var listed = new List<VersionNode>();
    var listReads = Reads(() => listed = store.Nodes(Collection, recordIds[6_000]).ToList());
    Assert.Equal(versions, listed.Count);
    Assert.Equal(versionIds[6_000], listed.Select(node => node.VersionId));
    Assert.True(listReads <= height + 2 + versions, $"listing one record read {listReads} pages");
    var rangeReads = Reads(() => {
      var encoded = KeyEncoder.Encode(recordIds[6_000]);
      Assert.Equal(versions, tree.Range(CompositeKey.ValuePrefix(encoded), CompositeKey.AboveValuePrefix(encoded)).Count());
    });
    Assert.True(rangeReads <= height + 2, $"the range read {rangeReads} index pages");

    //An operation: one descent.
    var sample = operationIds[batches / 2];
    var operationKey = CompositeKey.Encode(sample, sample);
    var findReads = Reads(() => Assert.NotNull(tree.Find(operationKey)));
    Assert.True(findReads <= height, $"Operation(id) read {findReads} index pages");
    Operation operation = null;
    var operationReads = Reads(() => operation = store.Operation(Collection, sample));
    Assert.Equal(sample, operation!.Id);
    Assert.True(operationReads <= height + 1, $"the store's Operation read {operationReads} pages");
    //And no record's range holds it.
    Assert.Null(store.Node(Collection, sample, sample));
  }

  //HS-8 and V-6: the pointer is checked before use and the index answers when the check fails.
  [Fact]
  public void APointerToTheWrongNodeFallsBackToTheIndex() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var store = Store(db);
    var recordA = RecordIdentity.Next();
    var recordB = RecordIdentity.Next();
    var versionA = RecordIdentity.Next();
    var versionB = RecordIdentity.Next();
    var operationId = RecordIdentity.Next();
    DocumentAddress addressA = default;
    DocumentAddress addressB = default;
    db.InTransaction(() => {
      addressA = store.WriteNode(Collection, Node(recordA, versionA, null, operationId, 0, VersionKind.Insert));
      addressB = store.WriteNode(Collection, Node(recordB, versionB, null, operationId, 0, VersionKind.Insert));
    });

    //The right pointer: the node, with no descent of the index.
    var right = new RecordHeader { RecordId = recordA, VersionId = versionA, PreviousVersion = addressA };
    var before = db.PageReadCount;
    Assert.Equal(versionA, store.HeadNode(Collection, right).VersionId);
    Assert.True(db.PageReadCount - before <= 1, "a good pointer needs one page");

    //A pointer at another record's node, at a free slot, and at a page that is not there.
    var wrongNode = new RecordHeader { RecordId = recordA, VersionId = versionA, PreviousVersion = addressB };
    Assert.Equal(versionA, store.HeadNode(Collection, wrongNode).VersionId);
    var freeSlot = new RecordHeader { RecordId = recordA, VersionId = versionA, PreviousVersion = new DocumentAddress(addressA.PageIndex, 200) };
    Assert.Equal(versionA, store.HeadNode(Collection, freeSlot).VersionId);
    var noPage = new RecordHeader { RecordId = recordA, VersionId = versionA, PreviousVersion = new DocumentAddress(uint.MaxValue - 1, 0) };
    Assert.Equal(versionA, store.HeadNode(Collection, noPage).VersionId);
    var zero = new RecordHeader { RecordId = recordA, VersionId = versionA, PreviousVersion = default };
    Assert.Equal(addressA, store.HeadNode(Collection, zero).Address);

    //A record with no node at all: nothing, from either route.
    var unknown = new RecordHeader { RecordId = RecordIdentity.Next(), VersionId = RecordIdentity.Next(), PreviousVersion = addressA };
    Assert.Null(store.HeadNode(Collection, unknown));
  }

  [Fact]
  public void DroppingHistoryDropsTheIndexAndAFreshHistoryStartsEmpty() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var store = Store(db);
    var recordId = RecordIdentity.Next();
    var operationId = RecordIdentity.Next();
    db.InTransaction(() => {
      Ulid? parent = null;
      for (var v = 0; v < 50; v++) {
        var versionId = RecordIdentity.Next();
        store.WriteNode(Collection, Node(recordId, versionId, parent, operationId, v));
        parent = versionId;
      }
    });
    var historyName = HistoryCollections.NameFor(db.Collection(Collection).Id);
    Assert.NotEqual(0u, db.Collection(historyName).VersionIndexRoot);
    Assert.Equal(50, store.Nodes(Collection, recordId).Count);

    db.SetRetentionPolicy(Collection, RetentionPolicy.None, dropHistory: true);
    Assert.DoesNotContain(db.Collections, collection => collection.Name == historyName);
    Assert.Throws<InvalidOperationException>(() => store.Nodes(Collection, recordId));

    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    //A fresh history: nothing of the record, and in the index only what switch-on records — the
    //schema and relation nodes (HS-10).
    Assert.Empty(store.Nodes(Collection, recordId));
    Assert.Null(store.Head(Collection, recordId));
    Assert.Equal(store.SchemaNodes(Collection).Count + store.RelationNodes(Collection).Count,
      store.VersionIndex(Collection).Scan().Count());
  }
}
