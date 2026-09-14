using TokkDb.Pages;
using TokkDb.Pages.Versions;
using Xunit;

namespace TokkDb.Tests;

//HS-6, HS-9, V-7 and V-12: one operation per outermost unit of work that records a version,
//stamped with the attribution scope open when it began.
public class AttributionTests {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewVersioned(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    return db;
  }

  //Every operation document in the collection's history, read through the system store: the
  //test is about how many there are, which the index cannot say without a scan.
  private static List<Operation> Operations(TokkDbConnection db) {
    var historyName = HistoryCollections.NameFor(db.Collection(Collection).Id);
    return db.SystemDocuments.ReadAll(historyName)
      .Where(entry => HistoryDocuments.TypeOf(entry.Document) == HistoryDocuments.OperationType)
      .Select(entry => db.Versions.Operation(Collection, entry.Id))
      .ToList();
  }

  private static Operation OperationOf(TokkDbConnection db, Ulid recordId) {
    return db.Versions.Operation(Collection, db.Versions.Head(Collection, recordId).OperationId);
  }

  [Fact]
  public void ThreeRecordingsInOneUnitOfWorkYieldOneOperation() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var people = db.Entities<Person>();
    var ids = new List<Ulid>();
    db.InTransaction(() => {
      ids.Add(people.Insert(TestPeople.Numbered(1)));
      //A nested unit of work belongs to the outermost one: still the same operation.
      db.InTransaction(() => ids.Add(people.Insert(TestPeople.Numbered(2))));
      ids.Add(people.Insert(TestPeople.Numbered(3)));
    });

    var operation = Assert.Single(Operations(db));
    var heads = ids.Select(id => db.Versions.Head(Collection, id)).ToList();
    Assert.All(heads, head => Assert.Equal(operation.Id, head.OperationId));
    Assert.Equal(string.Empty, operation.Author);
    Assert.Equal(default, operation.Cause);
    Assert.Equal(string.Empty, operation.Comment);
    Assert.True(operation.RecordedAt > DateTime.UtcNow.AddMinutes(-1) && operation.RecordedAt <= DateTime.UtcNow);
  }

  [Fact]
  public void TwoUnitsOfWorkYieldTwoOperationsAndARolledBackOneLeavesNone() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var people = db.Entities<Person>();
    var first = people.Insert(TestPeople.Numbered(1));
    var second = people.Insert(TestPeople.Numbered(2));
    Assert.Equal(2, Operations(db).Count);
    Assert.NotEqual(OperationOf(db, first).Id, OperationOf(db, second).Id);

    Ulid doomed = default;
    Assert.Throws<InvalidOperationException>(() => db.InTransaction(() => {
      doomed = people.Insert(TestPeople.Numbered(3));
      throw new InvalidOperationException("rolled back on purpose");
    }));

    Assert.Equal(2, Operations(db).Count);
    Assert.Null(db.Versions.Head(Collection, doomed));
    Assert.Empty(db.Versions.Nodes(Collection, doomed));
    Assert.Equal(2, people.GetAll().Count());
  }

  [Fact]
  public void AScopeStampsEveryUnitOfWorkThatBeginsInsideIt() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var people = db.Entities<Person>();
    var cause = Ulid.NewUlid();
    var inside = new List<Ulid>();
    using (db.Attribute(new VersionAttribution("olexander", cause, "importing the spreadsheet"))) {
      for (var i = 0; i < 3; i++) {
        inside.Add(people.Insert(TestPeople.Numbered(i)));
      }
      //A second scope inside the first is refused rather than silently ignored.
      Assert.Throws<InvalidOperationException>(() => db.Attribute(new VersionAttribution("someone else", default)));
    }
    var outside = people.Insert(TestPeople.Numbered(10));

    //Three units of work: three operations that share the scope's attribution.
    var operations = inside.Select(id => OperationOf(db, id)).ToList();
    Assert.Equal(3, operations.Select(operation => operation.Id).Distinct().Count());
    Assert.All(operations, operation => {
      Assert.Equal("olexander", operation.Author);
      Assert.Equal(cause, operation.Cause);
      Assert.Equal("importing the spreadsheet", operation.Comment);
    });
    //Outside any scope: unknown, not a guess.
    var unattributed = OperationOf(db, outside);
    Assert.Equal(string.Empty, unattributed.Author);
    Assert.Equal(default, unattributed.Cause);

    //And after the scope closed, a new one can open.
    using (db.Attribute(new VersionAttribution("again", default))) {
      var again = people.Insert(TestPeople.Numbered(11));
      Assert.Equal("again", OperationOf(db, again).Author);
    }
  }

  //The attribution is taken as the unit of work begins: a scope opened after that changes
  //nothing for it.
  [Fact]
  public void TheAttributionIsTheOneInForceWhenTheUnitOfWorkBegan() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var people = db.Entities<Person>();
    Ulid id = default;
    db.InTransaction(() => {
      using (db.Attribute(new VersionAttribution("late", default))) {
        id = people.Insert(TestPeople.Numbered(1));
      }
    });
    Assert.Equal(string.Empty, OperationOf(db, id).Author);
  }
}
