using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using Xunit;

namespace TokkDb.Tests;

//HS-2 and V-14: the history collection comes and goes with the policy, in one transaction.
public class HistoryCollectionTests {
  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    for (var i = 0; i < 5; i++) {
      db.Entities<Person>().Insert(TestPeople.Numbered(i));
    }
    return db;
  }

  private static string HistoryName(TokkDbConnection db, string collection) {
    return HistoryCollections.NameFor(db.Collection(collection).Id);
  }

  [Fact]
  public void TurningVersioningOnCreatesTheHistoryCollectionAndLinksIt() {
    using var file = new TempDatabaseFile();
    Ulid historyId;
    using (var db = NewDatabase(file)) {
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);

      var person = db.Collection(nameof(Person));
      Assert.NotEqual(default, person.HistoryCollectionId);
      var history = db.Collection(HistoryName(db, nameof(Person)));
      Assert.Equal(person.HistoryCollectionId, history.Id);
      Assert.True(history.IsSystem);
      Assert.NotEqual(person.OwningCollectionId, history.OwningCollectionId);
      historyId = history.Id;

      //Switching on again with other settings keeps the history it has.
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions, snapshotInterval: 4);
      Assert.Equal(historyId, db.Collection(nameof(Person)).HistoryCollectionId);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(historyId, reopened.Collection(nameof(Person)).HistoryCollectionId);
    Assert.Equal(historyId, reopened.Collection(HistoryName(reopened, nameof(Person))).Id);
    Assert.Equal(5, reopened.Entities<Person>().GetAll().Count());
  }

  //HS-2: a failure between writing the policy and creating the history collection leaves
  //neither, because both are one transaction and recovery takes the whole of it back.
  [Fact]
  public void AFailureBetweenThePolicyAndTheHistoryCollectionLeavesNeither() {
    int writesInACleanRun;
    using (var file = new TempDatabaseFile()) {
      using (var db = NewDatabase(file)) { }
      using var disk = new FaultInjectingDiskManager(file.Path);
      using (var db = new TokkDbConnection(disk)) {
        db.Load();
        db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
      }
      writesInACleanRun = disk.WriteCount;
    }
    Assert.True(writesInACleanRun > 2, $"only {writesInACleanRun} writes to aim at");

    var fired = 0;
    for (var killAfterWrites = 1; killAfterWrites <= writesInACleanRun; killAfterWrites++) {
      using var file = new TempDatabaseFile();
      using (var db = NewDatabase(file)) { }

      var disk = new FaultInjectingDiskManager(file.Path, killAfterWrites);
      var attempt = new TokkDbConnection(disk);
      try {
        attempt.Load();
        attempt.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
      } catch (SimulatedProcessKillException) {
        fired++;
      } finally {
        attempt.Dispose();
      }
      if (!disk.HasFired) {
        continue;
      }

      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var person = reopened.Collection(nameof(Person));
      Assert.Equal(RetentionPolicy.None, person.RetentionPolicy);
      Assert.Equal(default, person.HistoryCollectionId);
      Assert.DoesNotContain(reopened.Collections, collection => HistoryCollections.IsHistoryName(collection.Name));
      Assert.Equal(5, reopened.Entities<Person>().GetAll().Count());
    }
    Assert.True(fired > 0, "no run was interrupted");
  }

  //HS-2: history pages carry their own owning collection id, not the versioned collection's.
  [Fact]
  public void HistoryPagesCarryTheirOwnOwningId() {
    using var file = new TempDatabaseFile();
    uint historyOwner;
    uint personOwner;
    using (var db = NewDatabase(file)) {
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
      var name = HistoryName(db, nameof(Person));
      historyOwner = db.Collection(name).OwningCollectionId;
      personOwner = db.Collection(nameof(Person)).OwningCollectionId;
      //Nothing writes a node until Phase 3; a document written through the system store
      //stands in for one and lands on the history collection's own pages.
      var document = new ObjectDocument();
      var id = Ulid.NewUlid();
      document.SetIdentifierValue(new UlidDocumentValue(id));
      document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
        ["kind"] = new StringDocumentValue("stand-in")
      }));
      db.InTransaction(() => db.SystemDocuments.Write(name, id, document));
    }

    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var history = reader.Collection(HistoryName(reader, nameof(Person)));
    Assert.NotEqual(default, history.DataFirstPage);
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var page = pageManager.LoadPage<DataPage>(history.DataFirstPage);
    Assert.Equal(historyOwner, page.OwningCollectionId);
    Assert.NotEqual(personOwner, page.OwningCollectionId);
  }

  [Fact]
  public void TurningVersioningOffIsRefusedWhileHistoryExistsUnlessItIsDropped() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
      var name = HistoryName(db, nameof(Person));

      var refusal = Assert.Throws<InvalidOperationException>(() =>
        db.SetRetentionPolicy(nameof(Person), RetentionPolicy.None));
      Assert.Contains("dropHistory", refusal.Message);
      Assert.Equal(RetentionPolicy.KeepVersions, db.Collection(nameof(Person)).RetentionPolicy);
      Assert.True(db.Collections.Any(collection => collection.Name == name));

      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.None, dropHistory: true);

      Assert.Equal(RetentionPolicy.None, db.Collection(nameof(Person)).RetentionPolicy);
      Assert.Equal(default, db.Collection(nameof(Person)).HistoryCollectionId);
      Assert.DoesNotContain(db.Collections, collection => collection.Name == name);
      Assert.Equal(5, db.Entities<Person>().GetAll().Count());

      //And on again: a fresh history collection.
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
      Assert.NotEqual(default, db.Collection(nameof(Person)).HistoryCollectionId);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(RetentionPolicy.KeepVersions, reopened.Collection(nameof(Person)).RetentionPolicy);
    Assert.Single(reopened.Collections, collection => HistoryCollections.IsHistoryName(collection.Name));
  }

  //None to None, with or without the flag, is nothing.
  [Fact]
  public void TurningOffWhatWasNeverOnChangesNothing() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.SetRetentionPolicy(nameof(Person), RetentionPolicy.None);
    db.SetRetentionPolicy(nameof(Person), RetentionPolicy.None, dropHistory: true);
    Assert.Equal(default, db.Collection(nameof(Person)).HistoryCollectionId);
    Assert.DoesNotContain(db.Collections, collection => HistoryCollections.IsHistoryName(collection.Name));
  }

  //HS-2: dropping the collection removes both from the catalogue.
  [Fact]
  public void DroppingTheCollectionDropsItsHistory() {
    using var file = new TempDatabaseFile();
    string name;
    using (var db = NewDatabase(file)) {
      db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
      name = HistoryName(db, nameof(Person));
      Assert.True(db.DropCollection(nameof(Person)));
      Assert.DoesNotContain(db.Collections, collection => collection.Name == nameof(Person));
      Assert.DoesNotContain(db.Collections, collection => collection.Name == name);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.DoesNotContain(reopened.Collections, collection => collection.Name == nameof(Person));
    Assert.DoesNotContain(reopened.Collections, collection => collection.Name == name);
  }

  //The reserved prefix stays refused for anything but a history collection through the
  //internal paths, and the public paths never accept it.
  [Fact]
  public void TheHistoryCollectionCannotBeMadeOrDroppedThroughThePublicPaths() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.SetRetentionPolicy(nameof(Person), RetentionPolicy.KeepVersions);
    var name = HistoryName(db, nameof(Person));
    Assert.Throws<ReservedCollectionNameException>(() => db.DropCollection(name));
    Assert.Throws<ReservedCollectionNameException>(() => db.CreateCollection("_history:other"));
    Assert.Throws<ReservedCollectionNameException>(() => db.SetRetentionPolicy(name, RetentionPolicy.KeepVersions));
  }
}
