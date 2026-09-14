using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RB-1, RB-3, RB-4, V-9, G-4 and G-5: a restore adds a child of the restored version, refusals
//change nothing, and nothing readable before is lost.
public class RestoreTests {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, int k = 8) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, k, 1.0);
    return db;
  }

  private static Person Aged(int age, string name = "Person-1") {
    return new Person { Id = 1, Name = name, Age = age, Passport = new Passport("ST-1"), Tags = [new Tag("t")] };
  }

  //Every version of a record as it reads now: what G-4 compares before and after a restore.
  private static Dictionary<Ulid, string> Readings(DbEntities<Person> people, Ulid id) {
    return people.History(id).Versions.ToDictionary(version => version.VersionId, version => {
      var read = people.GetAsOf(id, version.VersionId);
      return read.IsDeleted ? "deleted" : $"{read.Value.Id}/{read.Value.Name}/{read.Value.Age}/{read.Value.Passport?.Code}";
    });
  }

  [Fact]
  public void RestoringV2OfFourAndEditingLeavesTwoLeavesBothReadable() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, k: 4);
    var people = db.Entities<Person>();
    var id = people.Insert(Aged(1));
    var versions = new List<Ulid> { people.HeadVersion(id) };
    foreach (var age in new[] { 2, 3, 4 }) {
      people.Update(id, Aged(age));
      versions.Add(people.HeadVersion(id));
    }
    var before = Readings(people, id);

    var result = people.Restore(id, versions[1]);

    Assert.Equal(versions[1], result.RestoredVersion);
    Assert.Equal(versions[3], result.ReplacedHead);
    Assert.False(result.WasDeleted);
    Assert.Empty(result.Unmapped);
    Assert.Equal(2, people.GetById(id).Value.Age);
    var restore = db.Versions.Node(Collection, id, result.NewVersion);
    Assert.Equal(VersionKind.Restore, restore.Kind);
    Assert.Equal(versions[1], restore.Parent);
    Assert.Equal(versions[3], restore.ReplacedHead);
    //The delta runs from the restored version to what was written: nothing, and the node is
    //there all the same (V-7).
    Assert.True(restore.Delta is null || restore.Delta.IsEmpty);

    people.Update(id, Aged(20));
    var edit = people.HeadVersion(id);

    var history = people.History(id);
    Assert.Equal([versions[3], edit], history.Leaves.Order());
    Assert.Equal(edit, history.Head);
    Assert.Equal(6, history.Versions.Count);
    Assert.Equal(4, people.GetAsOf(id, versions[3]).Value.Age);
    Assert.Equal(20, people.GetAsOf(id, edit).Value.Age);
    //WV-5 on both branches, and every version reads as it did before (G-4, G-5).
    foreach (var version in history.Versions) {
      var report = db.Versions.Reconstruct(Collection, id, version.VersionId).Report;
      Assert.True(report.DeltasApplied <= 3, $"{report}");
    }
    var after = Readings(people, id);
    foreach (var (version, reading) in before) {
      Assert.Equal(reading, after[version]);
    }
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());
  }

  //A restore made after a lossy retype reconstructs, through V-3, to exactly what GetById
  //returned right after it; a value in a since-removed column comes back as Unmapped.
  [Fact]
  public void ARestoreAcrossSchemaChangesReconstructsAndReportsWhatItCouldNotCarry() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    const string name = "Doc";
    db.CreateCollection(name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.String),
      new ColumnDescriptor("Note", ValueTypeEnum.String)]);
    var docs = db.Entities(new FieldMapSerializer(), name);
    var id = docs.Insert(new Dictionary<string, IDocumentValue> {
      ["Title"] = new StringDocumentValue("a"), ["Year"] = new StringDocumentValue("about 2000"), ["Note"] = new StringDocumentValue("keep")
    });
    var first = docs.HeadVersion(id);
    docs.Update(id, new Dictionary<string, IDocumentValue> {
      ["Title"] = new StringDocumentValue("b"), ["Year"] = new StringDocumentValue("2001"), ["Note"] = new StringDocumentValue("keep")
    });
    //A lossy retype for the first version, lossless for the second, and a removal.
    db.SetColumns(name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String)], [ColumnMigration.Retype(0, "Year", ValueTypeEnum.Int)]);
    db.SetColumns(name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int)],
      [ColumnMigration.Remove(0, "Note")]);

    var result = docs.Restore(id, first);

    Assert.Equal(2, result.Unmapped.Count);
    Assert.Contains(result.Unmapped, unmapped => unmapped.Reason == UnmappedReason.RetypeLossy && unmapped.Column == "Year");
    var removed = Assert.Single(result.Unmapped, unmapped => unmapped.Reason == UnmappedReason.ColumnRemoved);
    Assert.Equal("Note", removed.Column);
    Assert.Equal("keep", ((StringDocumentValue)removed.Value).Value);
    var restored = docs.GetById(id).Value;
    Assert.Equal("a", ((StringDocumentValue)restored["Title"]).Value);
    Assert.False(restored.ContainsKey("Year"));
    Assert.False(restored.ContainsKey("Note"));
    //The Restore node reconstructs to exactly what the live record reads as.
    var reconstruction = db.Versions.Reconstruct(name, id, result.NewVersion);
    Assert.True(CanonicalValue.Equal(new FieldMapSerializer().Create(restored, id).Value, reconstruction.Document.Value));
    Assert.True(db.Versions.Verify(name).IsSound);
  }

  [Fact]
  public void AUniqueRefusalNamesTheColumnAndTheHolderAndChangesNothing() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int), new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
      new ColumnDescriptor("Age", ValueTypeEnum.Int), new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ]);
    var people = db.Entities<Person>();
    var a = people.Insert(Aged(1, "x"));
    var aFirst = people.HeadVersion(a);
    people.Update(a, Aged(1, "y"));
    var aHead = people.HeadVersion(a);
    var b = people.Insert(Aged(2, "x"));
    var nodesBefore = db.Versions.Nodes(Collection, a).Count;

    var refusal = Assert.Throws<UniqueConstraintViolationException>(() => people.Restore(a, aFirst));

    Assert.Equal("Name", refusal.ColumnName);
    Assert.Equal(b, refusal.ConflictingRecordId);
    Assert.Equal(aHead, people.HeadVersion(a));
    Assert.Equal("y", people.GetById(a).Value.Name);
    Assert.Equal(nodesBefore, db.Versions.Nodes(Collection, a).Count);
    Assert.Single(people.GetBy("Name", "x"));
    Assert.Single(people.GetBy("Name", "y"));
    Assert.True(db.Versions.Verify(Collection).IsSound);
  }

  [Fact]
  public void ARelationRefusalChangesNothing() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true)]);
    db.CreateCollection(Collection, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int), new ColumnDescriptor("Name", ValueTypeEnum.String),
      new ColumnDescriptor("Age", ValueTypeEnum.Int), new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ]);
    db.CreateRelation("PersonCity", Collection, "Name", "City", "Name");
    var cities = db.Entities(new FieldMapSerializer(), "City");
    var lviv = cities.Insert(new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue("Lviv") });
    cities.Insert(new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue("Kyiv") });
    var people = db.Entities<Person>();
    var id = people.Insert(Aged(1, "Lviv"));
    var first = people.HeadVersion(id);
    people.Update(id, Aged(1, "Kyiv"));
    cities.Delete(lviv);

    Assert.Throws<Pages.Relations.ReferentialIntegrityException>(() => people.Restore(id, first));
    Assert.Equal("Kyiv", people.GetById(id).Value.Name);
    Assert.Equal(2, db.Versions.Nodes(Collection, id).Count);
  }

  [Fact]
  public void RestoringTheHeadOrATombstoneIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var people = db.Entities<Person>();
    var id = people.Insert(Aged(1));
    people.Update(id, Aged(2));
    var head = people.HeadVersion(id);

    var refusal = Assert.Throws<RestoreRefusedException>(() => people.Restore(id, head));
    Assert.Equal(RestoreRefusal.CurrentHead, refusal.Reason);
    Assert.Equal(2, db.Versions.Nodes(Collection, id).Count);

    people.Delete(id);
    var tombstone = people.HeadVersion(id);
    Assert.Equal(RestoreRefusal.Tombstone, Assert.Throws<RestoreRefusedException>(() => people.Restore(id, tombstone)).Reason);
    Assert.Equal(RestoreRefusal.NoLongerKept, Assert.Throws<RestoreRefusedException>(() => people.Restore(id, Ulid.NewUlid())).Reason);
    Assert.Null(people.GetById(id));
  }
}
