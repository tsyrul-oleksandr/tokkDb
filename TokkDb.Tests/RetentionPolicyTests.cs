using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Tests.Fixtures;
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
      Assert.Equal(RetentionPolicy.None, db.Collection(nameof(Person)).RetentionPolicy);

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

  [Fact]
  public void TheDefaultsAreNoneAndTheStandInsForTheUnmeasuredValues() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var descriptor = db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
    Assert.Equal(8, descriptor.SnapshotInterval);
    Assert.Equal(0.5, descriptor.LargeDeltaRatio);
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

    //Refused before anything was written.
    var descriptor = db.Collection(nameof(Person));
    Assert.Equal(RetentionPolicy.None, descriptor.RetentionPolicy);
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
