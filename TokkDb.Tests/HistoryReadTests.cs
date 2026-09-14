using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RH-1 and RH-2: the forest as an audit trail, and a version read through the current schema
//or as stored.
public class HistoryReadTests {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, snapshotInterval: 8, largeDeltaRatio: 1.0);
    return db;
  }

  private static Person Numbered(int i, int age) {
    return new Person { Id = i, Name = $"Person-{i}", Age = age, Passport = new Passport($"ST-{i}"), Tags = [new Tag($"t{i}")] };
  }

  [Fact]
  public void ThreeWritesInOneUnitOfWorkInsideOneScopeShowOneOperationWithItsAttribution() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var people = db.Entities<Person>();
    var cause = Ulid.NewUlid();
    var id = people.Insert(Numbered(1, 20));
    using (db.Attribute(new VersionAttribution("olexander", cause, "fixing the ages"))) {
      db.InTransaction(() => {
        people.Update(id, Numbered(1, 21));
        people.Update(id, Numbered(1, 22));
        people.Update(id, Numbered(1, 23));
      });
    }

    var history = people.History(id);
    Assert.Equal(4, history.Versions.Count);
    Assert.Equal(VersionKind.Insert, history.Versions[0].Kind);
    Assert.All(history.Versions.Skip(1), version => Assert.Equal(VersionKind.Update, version.Kind));
    var operation = Assert.Single(history.Versions.Skip(1).Select(version => version.OperationId).Distinct());
    Assert.NotEqual(history.Versions[0].OperationId, operation);
    Assert.All(history.Versions.Skip(1), version => {
      Assert.Equal("olexander", version.Author);
      Assert.Equal(cause, version.Cause);
      Assert.Equal("fixing the ages", version.Comment);
      Assert.True(version.RecordedAt > DateTime.UtcNow.AddMinutes(-1));
    });
    Assert.Equal(string.Empty, history.Versions[0].Author);
    //The shape: one root, one leaf, which is the head; written in order.
    Assert.Equal(history.Versions[^1].VersionId, history.Head);
    Assert.Equal([history.Versions[^1].VersionId], history.Leaves);
    Assert.Equal([history.Versions[0].VersionId], history.Roots);
    Assert.True(history.Versions[^1].IsHead);
    Assert.True(history.Versions[^1].IsLeaf);
    Assert.False(history.Versions[0].IsLeaf);
    Assert.False(history.IsDeleted);
    Assert.Empty(history.OperationsRecordedOutOfOrder);
    for (var i = 1; i < history.Versions.Count; i++) {
      Assert.Equal(history.Versions[i - 1].VersionId, history.Versions[i].Parent);
      Assert.True(history.Versions[i].VersionId.CompareTo(history.Versions[i - 1].VersionId) > 0);
    }
  }

  [Fact]
  public void EachVersionEqualsWhatGetByIdReturnedRightAfterItsWrite() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var people = db.Entities<Person>();
    var id = people.Insert(Numbered(1, 20));
    var seen = new List<(Ulid Version, Person Value)> { (people.HeadVersion(id), people.GetById(id).Value) };
    for (var step = 1; step <= 12; step++) {
      people.Update(id, step % 5 == 0
        ? new Person { Id = 100 + step, Name = $"Other-{step}", Age = step, Passport = new Passport("X"), Tags = [] }
        : Numbered(1, 20 + step));
      seen.Add((people.HeadVersion(id), people.GetById(id).Value));
    }

    foreach (var (version, value) in seen) {
      var read = people.GetAsOf(id, version);
      Assert.False(read.IsDeleted);
      Assert.Equal(version, read.Version.VersionId);
      Assert.Equal(value.Id, read.Value.Id);
      Assert.Equal(value.Name, read.Value.Name);
      Assert.Equal(value.Age, read.Value.Age);
      Assert.Equal(value.Passport?.Code, read.Value.Passport?.Code);
      Assert.Equal(value.Tags.Select(tag => tag.Name), read.Value.Tags.Select(tag => tag.Name));
      Assert.Empty(read.Unmapped);
    }
    //And the deleted record: its versions still read, its head is the tombstone.
    people.Delete(id);
    var history = people.History(id);
    Assert.True(history.IsDeleted);
    Assert.Equal(VersionKind.Delete, history.Versions[^1].Kind);
    Assert.True(people.GetAsOf(id, history.Head!.Value).IsDeleted);
    Assert.Equal(seen[3].Value.Age, people.GetAsOf(id, seen[3].Version).Value.Age);
  }

  [Fact]
  public void AVersionFromBeforeARenameShowsTheOldNameAsStoredAndTheNewNameThroughTheSchema() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    const string name = "Doc";
    db.CreateCollection(name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int)]);
    db.SetRetentionPolicy(name, RetentionPolicy.KeepVersions);
    var docs = db.Entities(new FieldMapSerializer(), name);
    var id = docs.Insert(new Dictionary<string, IDocumentValue> { ["Title"] = new StringDocumentValue("a"), ["Year"] = new IntDocumentValue(2000) });
    var first = docs.HeadVersion(id);
    db.SetColumns(name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Published", ValueTypeEnum.Int)],
      [ColumnMigration.Rename(0, "Year", "Published")]);
    docs.Update(id, new Dictionary<string, IDocumentValue> { ["Title"] = new StringDocumentValue("b"), ["Published"] = new IntDocumentValue(2000) });

    var stored = docs.GetStoredAsOf(id, first);
    Assert.Equal(1, stored.SchemaVersion);
    var storedFields = (ObjectDocumentValue)stored.Document.Value;
    Assert.True(storedFields.Values.ContainsKey("Year"));
    Assert.False(storedFields.Values.ContainsKey("Published"));
    Assert.Equal(first, stored.Version.VersionId);
    Assert.False(stored.Version.IsHead);

    var mapped = docs.GetAsOf(id, first);
    Assert.True(mapped.Value.ContainsKey("Published"));
    Assert.False(mapped.Value.ContainsKey("Year"));
    Assert.Equal(2000, ((IntDocumentValue)mapped.Value["Published"]).Value);
    Assert.Equal("a", ((StringDocumentValue)mapped.Value["Title"]).Value);

    //A collection that keeps no versions has no history to read.
    db.CreateCollection("Plain", [new ColumnDescriptor("Title", ValueTypeEnum.String)]);
    var plain = db.Entities(new FieldMapSerializer(), "Plain");
    var plainId = plain.Insert(new Dictionary<string, IDocumentValue> { ["Title"] = new StringDocumentValue("p") });
    Assert.True(plain.History(plainId).IsEmpty);
    Assert.Throws<InvalidOperationException>(() => plain.GetStoredAsOf(plainId, Ulid.NewUlid()));
    Assert.Throws<VersionNotFoundException>(() => docs.GetAsOf(id, Ulid.NewUlid()));
  }
}
