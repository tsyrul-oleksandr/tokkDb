using TokkDb.Disk;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//WV-1, WV-2, WV-5, WV-6, HS-7 and HS-8: what an insert and an update record under KeepVersions,
//and what they leave exactly as under None.
public class VersionRecordingTests(ITestOutputHelper output) {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, RetentionPolicy policy, int k = 8,
      double ratio = 0.5) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    //Created versioned (I-4): None means dropping the empty history it came with.
    db.SetRetentionPolicy(Collection, policy, k, ratio, dropHistory: policy == RetentionPolicy.None);
    return db;
  }

  private static Person Small(int id, int step) {
    return new Person {
      Id = id, Name = $"Person-{id}", Age = 20 + step,
      Passport = new Passport($"ST-{id:D6}"), Tags = [new Tag($"tag-{id}")]
    };
  }

  //Every field different: a delta as large as the record.
  private static Person Rewritten(int id, int step) {
    return new Person {
      Id = id + 1000 * step, Name = $"Rewritten-{id}-{step}", Age = 60 + step,
      Passport = new Passport($"RW-{step:D6}-{id}"),
      Tags = [new Tag($"rewrite-{step}"), new Tag($"again-{step}"), new Tag($"and-{step}")]
    };
  }

  //The data pages of a collection as they lie in the file: one entry per page of the chain,
  //read after the connection has closed.
  private static List<(ushort Items, ushort Reclaimable)> DataChain(TempDatabaseFile file, string collection) {
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var first = reader.Collection(collection).DataFirstPage;
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var chain = new List<(ushort, ushort)>();
    for (var next = first; next != default;) {
      var page = pageManager.LoadPage<DataPage>(next);
      chain.Add((page.ItemsCount, page.ReclaimableBytes));
      next = page.NextPageIndex;
    }
    return chain;
  }

  private static List<RecordHeader> LiveHeaders(TempDatabaseFile file, string collection) {
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var first = reader.Collection(collection).DataFirstPage;
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var headers = new List<RecordHeader>();
    for (var next = first; next != default;) {
      var page = pageManager.LoadPage<DataPage>(next);
      headers.AddRange(page.GetItems().Select(StoredRecordUtilities.ReadHeader).Where(header => header.IsLive));
      next = page.NextPageIndex;
    }
    return headers;
  }

  [Fact]
  public void TenThousandInsertsAddTenThousandNodesOneOperationAndNoImage() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, RetentionPolicy.KeepVersions);
    var people = db.Entities<Person>();
    var ids = new List<Ulid>();
    db.InTransaction(() => {
      for (var i = 0; i < 10_000; i++) {
        ids.Add(people.Insert(TestPeople.Numbered(i)));
      }
    });

    var historyName = HistoryCollections.NameFor(db.Collection(Collection).Id);
    var documents = db.SystemDocuments.ReadAll(historyName).Select(entry => HistoryDocuments.TypeOf(entry.Document)).ToList();
    Assert.Equal(10_000, documents.Count(type => type == HistoryDocuments.NodeType));
    Assert.Equal(1, documents.Count(type => type == HistoryDocuments.OperationType));
    foreach (var id in ids.Where((_, i) => i % 997 == 0)) {
      var node = Assert.Single(db.Versions.Nodes(Collection, id));
      Assert.Equal(VersionKind.Insert, node.Kind);
      Assert.Null(node.Image);
      Assert.Null(node.Delta);
      Assert.Equal(0, node.Distance);
      Assert.Equal(node.VersionId, people.HeadVersion(id));
    }
  }

  [Fact]
  public void AFirstUpdateStoresTheRootImageAndOneDelta() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, RetentionPolicy.KeepVersions);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(1, 0));
    var first = people.HeadVersion(id);

    people.Update(id, Small(1, 1));

    var nodes = db.Versions.Nodes(Collection, id);
    Assert.Collection(nodes,
      root => {
        Assert.Equal(first, root.VersionId);
        Assert.Equal(VersionKind.Insert, root.Kind);
        Assert.NotNull(root.Image);
        Assert.Equal(1, root.ImageSchemaVersion);
        Assert.Null(root.Delta);
      },
      update => {
        Assert.Equal(VersionKind.Update, update.Kind);
        Assert.Equal(first, update.Parent);
        Assert.Equal(1, update.Distance);
        Assert.NotNull(update.Delta);
        Assert.Null(update.Image);
        Assert.Single(update.Delta.Elements);
        Assert.Equal("Age", update.Delta.Elements[0].Path.Render());
        Assert.True(update.VersionId.CompareTo(first) > 0);
        Assert.Equal(update.VersionId, people.HeadVersion(id));
      });
    Assert.Equal(21, Assert.Single(people.GetAll()).Age);
  }

  [Fact]
  public void AnUpdateThatChangesNothingAddsNoNodeAndNoImage() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, RetentionPolicy.KeepVersions);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(1, 0));
    var head = people.HeadVersion(id);

    people.Update(id, Small(1, 0));

    Assert.Equal(head, people.HeadVersion(id));
    Assert.Single(db.Versions.Nodes(Collection, id));
  }

  [Fact]
  public void ACompleteRewriteStoresAnImageRatherThanADelta() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, RetentionPolicy.KeepVersions, k: 32, ratio: 0.5);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(1, 0));
    people.Update(id, Rewritten(1, 1));
    var rewrite = db.Versions.Head(Collection, id);
    Assert.Equal(0, rewrite.Distance);
    Assert.Null(rewrite.Delta);
    //The head's image is live; once it leaves the head, the keyframe holds it.
    Assert.Null(rewrite.Image);
    people.Update(id, Rewritten(1, 2));
    var left = db.Versions.Node(Collection, id, rewrite.VersionId);
    Assert.NotNull(left.Image);
    Assert.Equal("Rewritten-1-1", ((Documents.Values.StringDocumentValue)left.Image.Values["Name"]).Value);
    //A small change after it is a delta at distance 1.
    people.Update(id, new Person { Id = 2001, Name = "Rewritten-1-2", Age = 63, Passport = new Passport("RW-000002-1"),
      Tags = [new Tag("rewrite-2"), new Tag("again-2"), new Tag("and-2")] });
    Assert.Equal(1, db.Versions.Head(Collection, id).Distance);
  }

  [Fact]
  public void TheDataChainMatchesTheSameWorkloadUnderNone() {
    List<(ushort, ushort)> Run(RetentionPolicy policy) {
      using var file = new TempDatabaseFile();
      using (var db = NewDatabase(file, policy)) {
        var people = db.Entities<Person>();
        var ids = new List<Ulid>();
        for (var i = 0; i < 60; i++) {
          ids.Add(people.Insert(Small(i, 0)));
        }
        for (var step = 1; step <= 3; step++) {
          foreach (var (id, i) in ids.Select((id, i) => (id, i))) {
            people.Update(id, i % 7 == 0 ? Rewritten(i, step) : Small(i, step));
          }
        }
        Assert.Equal(60u, db.Collection(Collection).RecordCount);
      }
      return DataChain(file, Collection);
    }

    var none = Run(RetentionPolicy.None);
    var kept = Run(RetentionPolicy.KeepVersions);
    output.WriteLine($"{none.Count} data pages under None, {kept.Count} under KeepVersions");
    Assert.Equal(none, kept);
  }

  //WV-6: a scan touches the collection's own chain and nothing of its history.
  [Fact]
  public void AScanReadsNoHistoryPage() {
    using var file = new TempDatabaseFile();
    long reads;
    using (var db = NewDatabase(file, RetentionPolicy.KeepVersions)) {
      var people = db.Entities<Person>();
      var ids = new List<Ulid>();
      for (var i = 0; i < 300; i++) {
        ids.Add(people.Insert(Small(i, 0)));
      }
      foreach (var id in ids) {
        people.Update(id, Small(0, 1) is var _ ? Small(ids.IndexOf(id), 1) : null);
      }
      var before = db.PageReadCount;
      Assert.Equal(300, people.GetAll().Count());
      reads = db.PageReadCount - before;
    }
    var chain = DataChain(file, Collection);
    output.WriteLine($"{reads} pages read to scan a chain of {chain.Count}");
    Assert.Equal(chain.Count, reads);
  }

  //HS-8: after every write the live image's pointer addresses the head's node.
  [Fact]
  public void EveryWriteLeavesThePointerAtTheHeadsNode() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file, RetentionPolicy.KeepVersions)) {
      var people = db.Entities<Person>();
      var ids = Enumerable.Range(0, 5).Select(i => people.Insert(Small(i, 0))).ToList();
      people.Update(ids[1], Small(1, 1));
      people.Update(ids[1], Small(1, 2));
      people.Update(ids[2], Rewritten(2, 1));
    }
    var headers = LiveHeaders(file, Collection);
    Assert.Equal(5, headers.Count);
    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    foreach (var header in headers) {
      Assert.NotEqual(default, header.PreviousVersion);
      var node = reopened.Versions.Node(Collection, header.RecordId, header.VersionId);
      Assert.NotNull(node);
      Assert.Equal(node.Address, header.PreviousVersion);
      Assert.Equal(node.VersionId, reopened.Versions.Head(Collection, header.RecordId).VersionId);
    }
  }

  //WV-5's bound on stored distances, for four intervals over 200 updates with complete rewrites
  //among them, and V-1's rule that a keyframe stores no delta and, once it has left the head,
  //holds its image.
  [Theory]
  [InlineData(1)]
  [InlineData(4)]
  [InlineData(8)]
  [InlineData(32)]
  public void DistancesStayBelowKAndKeyframesHoldImagesAndNoDelta(int k) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, RetentionPolicy.KeepVersions, k, 0.5);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(1, 0));
    for (var step = 1; step <= 200; step++) {
      people.Update(id, step % 37 == 0 ? Rewritten(1, step) : Small(1, step));
    }

    var nodes = db.Versions.Nodes(Collection, id);
    Assert.Equal(201, nodes.Count);
    var head = people.HeadVersion(id);
    Ulid? previous = null;
    foreach (var node in nodes) {
      Assert.True(node.Distance < k, $"distance {node.Distance} at k = {k}");
      Assert.Equal(previous, node.Parent);
      if (node.Distance == 0) {
        Assert.Null(node.Delta);
        if (node.VersionId != head) {
          Assert.NotNull(node.Image);
        }
      } else {
        Assert.NotNull(node.Delta);
        Assert.Null(node.Image);
      }
      previous = node.VersionId;
    }
    Assert.Equal(head, previous);
    var keyframes = nodes.Count(node => node.Distance == 0);
    output.WriteLine($"k = {k}: {keyframes} keyframes among {nodes.Count} nodes");
    Assert.True(keyframes >= 201 / k, "too few keyframes for the interval");
    if (k == 1) {
      Assert.All(nodes, node => Assert.Null(node.Delta));
    }
  }

  [Fact]
  public void LoweringKPartwayRewritesNoNodeAndKeepsBothBounds() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, RetentionPolicy.KeepVersions, k: 32, ratio: 0.5);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(1, 0));
    for (var step = 1; step <= 100; step++) {
      people.Update(id, Small(1, step));
    }
    var before = db.Versions.Nodes(Collection, id)
      .Select(node => (node.VersionId, node.Distance, node.Image is not null, node.Delta is not null, node.Address)).ToList();
    Assert.Contains(before, node => node.Distance > 4);

    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, snapshotInterval: 4, largeDeltaRatio: 0.5);

    var after = db.Versions.Nodes(Collection, id)
      .Select(node => (node.VersionId, node.Distance, node.Image is not null, node.Delta is not null, node.Address)).ToList();
    Assert.Equal(before, after);

    for (var step = 101; step <= 200; step++) {
      people.Update(id, Small(1, step));
    }
    var nodes = db.Versions.Nodes(Collection, id);
    Assert.Equal(201, nodes.Count);
    //The first hundred keep the bound they were written under; the rest keep the new one, and
    //the first write under it became a keyframe as soon as its distance would have reached 4.
    Assert.All(nodes.Take(101), node => Assert.True(node.Distance < 32));
    Assert.All(nodes.Skip(101), node => Assert.True(node.Distance < 4, $"distance {node.Distance} after lowering k"));
    Assert.Equal(0, nodes[101].Distance);
  }
}

//HS-7 across a restart: a version minted with the clock behind the last write still sorts after
//the head. Runs alone, because it resets the engine's identifier source.
[Collection(EngineClockCollection.Name)]
public class VersionRecordingAcrossRestartTests {
  private const string Collection = nameof(Person);

  [Fact]
  public void AfterAReopenWithTheClockAnHourBehindTheNextUpdateSortsAfterTheHead() {
    using var file = new TempDatabaseFile();
    Ulid id;
    Ulid head;
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
      var people = db.Entities<Person>();
      id = people.Insert(TestPeople.Numbered(1));
      people.Update(id, TestPeople.Numbered(2));
      head = people.HeadVersion(id);
    }

    RecordIdentity.ResetForTests();
    using (EngineClock.Override(() => DateTimeOffset.UtcNow.AddHours(-1))) {
      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var people = reopened.Entities<Person>();
      people.Update(id, TestPeople.Numbered(3));
      var next = people.HeadVersion(id);
      Assert.True(next.CompareTo(head) > 0, $"{next} does not sort after the head {head}");
      var nodes = reopened.Versions.Nodes(Collection, id);
      Assert.Equal(3, nodes.Count);
      Assert.Equal(head, nodes[2].Parent);
      Assert.Equal(next, nodes[2].VersionId);
    }
  }
}
