using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Tests.Fixtures;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//HS-1 and V-13: the retention settings live in the collection's catalogue document and
//nowhere else.
public class RetentionPolicyTests {
  [Fact]
  public void TheSettingsSurviveAReopen() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      //I-4: a new user collection keeps versions from the start.
      Assert.Equal(RetentionPolicy.KeepVersions, db.Collection(nameof(Person)).RetentionPolicy);

      var descriptor = db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions, snapshotInterval: 16,
        largeDeltaRatio: 0.75);

      Assert.Equal(RetentionPolicy.KeepVersions, descriptor.RetentionPolicy);
      Assert.Equal(16, descriptor.SnapshotInterval);
      Assert.Equal(0.75, descriptor.LargeDeltaRatio);
      Assert.Equal(RetentionPolicy.KeepVersions, db.Entities<Person>().RetentionPolicy);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var read = reopened.Collection(nameof(Person));
    Assert.Equal(RetentionPolicy.KeepVersions, read.RetentionPolicy);
    Assert.Equal(16, read.SnapshotInterval);
    Assert.Equal(0.75, read.LargeDeltaRatio);
    Assert.Equal(RetentionPolicy.KeepVersions, reopened.Entities<Person>().RetentionPolicy);
  }

  //I-1, I-2 and I-4: a user collection created through the connection keeps versions at k = 8
  //and ratio 0.5, with its history collection made in the same transaction; a reserved one does
  //not.
  [Fact]
  public void ANewUserCollectionKeepsVersionsAtTheAcceptedDefaults() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true)]);
      foreach (var name in new[] { nameof(Person), "City" }) {
        var descriptor = db.Collection(name);
        Assert.Equal(RetentionPolicy.KeepVersions, descriptor.RetentionPolicy);
        Assert.Equal(8, descriptor.SnapshotInterval);
        Assert.Equal(0.5, descriptor.LargeDeltaRatio);
        Assert.NotEqual(default, descriptor.HistoryCollectionId);
        Assert.Contains(db.Collections, collection => collection.Id == descriptor.HistoryCollectionId);
      }
      Assert.All(db.Collections.Where(collection => collection.IsSystem),
        collection => Assert.Equal(RetentionPolicy.None, collection.RetentionPolicy));
      //And the first write records a version without anyone asking for it (VR-1).
      var people = db.Entities<Person>();
      var id = people.Insert(TestPeople.Ivan());
      Assert.Single(db.Versions.Nodes(nameof(Person), id));
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(RetentionPolicy.KeepVersions, reopened.Collection("City").RetentionPolicy);
  }

  //The collection and its history commit together: a failure anywhere leaves neither.
  [Fact]
  public void ACollectionAndItsHistoryAreCreatedTogether() {
    int writes;
    using (var file = new TempDatabaseFile()) {
      using (var db = new TokkDbConnection(file.Path)) {
        db.CreateDatabase(config => config.CreateEntity<Person>());
      }
      using var disk = new FaultInjectingDiskManager(file.Path);
      using (var db = new TokkDbConnection(disk)) {
        db.Load();
        db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String)]);
      }
      writes = disk.WriteCount;
    }
    var fired = 0;
    for (var killAfter = 1; killAfter <= writes; killAfter++) {
      using var file = new TempDatabaseFile();
      using (var db = new TokkDbConnection(file.Path)) {
        db.CreateDatabase(config => config.CreateEntity<Person>());
      }
      var disk = new FaultInjectingDiskManager(file.Path, killAfter);
      var attempt = new TokkDbConnection(disk);
      try {
        attempt.Load();
        attempt.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String)]);
      } catch (SimulatedProcessKillException) {
        fired++;
      } finally {
        attempt.Dispose();
      }
      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var city = reopened.Collections.SingleOrDefault(collection => collection.Name == "City");
      var histories = reopened.Collections.Count(collection => Pages.Versions.HistoryCollections.IsHistoryName(collection.Name));
      if (city is null) {
        Assert.Equal(1, histories);
      } else {
        Assert.Equal(2, histories);
        Assert.Equal(RetentionPolicy.KeepVersions, city.RetentionPolicy);
        Assert.Contains(reopened.Collections, collection => collection.Id == city.HistoryCollectionId);
      }
    }
    Assert.True(fired > 0);
  }

  //A database written before this plan existed says nothing about retention, and that reads
  //as None.
  [Fact]
  public void TheFixtureReadsAsNone() {
    using var file = PreVersioningFixture.Copy();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    foreach (var name in new[] { PreVersioningFixture.Conferences, PreVersioningFixture.Expenses }) {
      var descriptor = db.Collection(name);
      Assert.Equal(RetentionPolicy.None, descriptor.RetentionPolicy);
      Assert.Equal(CollectionDescriptor.DefaultSnapshotInterval, descriptor.SnapshotInterval);
      Assert.Equal(CollectionDescriptor.DefaultLargeDeltaRatio, descriptor.LargeDeltaRatio);
      Assert.Equal(RetentionPolicy.None, db.Entities<PreVersioningFixture.Expense>(name).RetentionPolicy);
    }
  }

  [Fact]
  public void AReservedCollectionRefusesKeepVersions() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    Assert.Throws<ReservedCollectionNameException>(() =>
      db.SetRetentionPolicy(SystemCollections.Collections, RetentionPolicy.KeepVersions));
    Assert.Throws<ReservedCollectionNameException>(() =>
      db.SetRetentionPolicy(SystemCollections.Settings, RetentionPolicy.KeepVersions));
    Assert.Equal(RetentionPolicy.None, db.Collection(SystemCollections.Settings).RetentionPolicy);
  }

  [Theory]
  [InlineData(0, 0.5)]
  [InlineData(-3, 0.5)]
  [InlineData(8, 0.0)]
  [InlineData(8, -0.1)]
  [InlineData(8, 1.5)]
  [InlineData(8, double.NaN)]
  public void AnIntervalBelowOneOrARatioOutsideZeroToOneIsRefused(int interval, double ratio) {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());

    Assert.Throws<ArgumentOutOfRangeException>(() =>
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions, interval, ratio));

    //Refused before anything was written: the defaults the collection was created with stand.
    var descriptor = db.Collection(nameof(Person));
    Assert.Equal(RetentionPolicy.KeepVersions, descriptor.RetentionPolicy);
    Assert.Equal(CollectionDescriptor.DefaultSnapshotInterval, descriptor.SnapshotInterval);
    Assert.Equal(CollectionDescriptor.DefaultLargeDeltaRatio, descriptor.LargeDeltaRatio);
  }

  //The edges: k = 1 is the full-copy layout, and a ratio of 1 means only a delta as large as
  //its image makes a keyframe.
  [Fact]
  public void TheEdgesOfTheRangesAreAccepted() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var descriptor = db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions, 1, 1.0);
    Assert.Equal(1, descriptor.SnapshotInterval);
    Assert.Equal(1.0, descriptor.LargeDeltaRatio);
  }

  //A change of policy is a schema change: a plan made before it is refused afterwards (QM-2a).
  [Fact]
  public void SettingThePolicyMovesTheCatalogueVersion() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();
    var plan = entities.Explain(entities.Query().Build());

    db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);

    Assert.Throws<StalePlanException>(() => entities.Run(plan));
  }
}
