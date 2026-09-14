using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//WV-8 and WV-10: Rewrite changes representation and keeps what it would lose; Verify sees a
//sound history through all of it; the in-place paths cannot reach a versioned record.
public class VersionRewriteTests {
  private const string Collection = nameof(Person);

  private static List<ColumnDescriptor> PersonColumns() {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ];
  }

  //Two records written before versioning, two after; then the changes and the rewrite. The
  //collection is created versioned (I-4), so it is switched off first, history and all.
  private static (List<Ulid> Before, List<Ulid> After) Populate(TokkDbConnection db) {
    db.Load();
    db.CreateCollection(Collection, PersonColumns());
    db.SetRetentionPolicy(Collection, RetentionPolicy.None, dropHistory: true);
    var people = db.Entities<Person>();
    var before = Enumerable.Range(0, 2).Select(i => people.Insert(TestPeople.Numbered(i))).ToList();
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    var after = Enumerable.Range(10, 2).Select(i => people.Insert(TestPeople.Numbered(i))).ToList();
    //A version behind the head, so that a head is not a keyframe.
    people.Update(after[1], TestPeople.Numbered(12));
    return (before, after);
  }

  //One line per record: its versions with distance, image and address, and its head.
  private static List<string> Snapshot(TokkDbConnection db, IEnumerable<Ulid> ids) {
    var people = db.Entities<Person>();
    return ids.Select(id => $"{id}: head {people.HeadVersion(id)}; " + string.Join(", ",
      db.Versions.Nodes(Collection, id).Select(node =>
        $"{node.VersionId} d{node.Distance} {(node.Image is null ? "-" : "image")} @{node.Address.PageIndex}/{node.Address.SlotIndex}")))
      .ToList();
  }

  [Fact]
  public void ARewriteThatLosesNothingChangesNoNodeAndNoPointer() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    var (before, after) = Populate(db);
    var renamed = PersonColumns().Select(column => column.Name == "Age" ? new ColumnDescriptor("Years", ValueTypeEnum.Int) : column);
    db.SetColumns(Collection, renamed, [ColumnMigration.Rename(0, "Age", "Years")]);
    var snapshot = Snapshot(db, before.Concat(after));

    Assert.Equal(4, db.Rewrite(Collection));

    Assert.Equal(snapshot, Snapshot(db, before.Concat(after)));
    Assert.All(before, id => Assert.Empty(db.Versions.Nodes(Collection, id)));
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());
    Assert.Empty(db.Collection(Collection).Migrations);
  }

  [Fact]
  public void ARewriteAfterARemovalStoresThePreRewriteImageOnce() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    var (before, after) = Populate(db);
    var people = db.Entities<Person>();
    var heads = before.Concat(after).ToDictionary(id => id, id => people.HeadVersion(id));
    db.SetColumns(Collection, PersonColumns().Where(column => column.Name != "Passport"),
      [ColumnMigration.Remove(0, "Passport")]);

    Assert.Equal(4, db.Rewrite(Collection));

    var verification = db.Versions.Verify(Collection);
    Assert.True(verification.IsSound, verification.ToString());
    foreach (var id in before.Concat(after)) {
      //No version was created and the head is the same.
      Assert.Equal(heads[id], people.HeadVersion(id));
      var head = db.Versions.Head(Collection, id);
      Assert.NotNull(head);
      Assert.Equal(heads[id], head.VersionId);
      //The pre-rewrite image, with the removed value, is in the head's node.
      Assert.NotNull(head.Image);
      Assert.True(head.Image.Values.ContainsKey("Passport"), "the removed column's value was not kept");
      Assert.Equal(1, head.ImageSchemaVersion);
    }
    //A record with no node gained a Baseline, and only one; a record with nodes gained none.
    foreach (var id in before) {
      var node = Assert.Single(db.Versions.Nodes(Collection, id));
      Assert.Equal(VersionKind.Baseline, node.Kind);
    }
    Assert.Single(db.Versions.Nodes(Collection, after[0]));
    Assert.Equal(2, db.Versions.Nodes(Collection, after[1]).Count);
    //And the live records read without the column.
    Assert.All(people.GetAll(), person => Assert.Null(person.Passport));
    Assert.Empty(db.Collection(Collection).Migrations);

    //A second rewrite has nothing pending and changes nothing.
    var snapshot = Snapshot(db, before.Concat(after));
    Assert.Equal(0, db.Rewrite(Collection));
    Assert.Equal(snapshot, Snapshot(db, before.Concat(after)));
  }

  //A node's image, once stored, is never replaced: a later keyframe copy keeps the earlier one.
  [Fact]
  public void AnImageStoredByRewriteIsNotReplacedByALaterCopy() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    var (before, after) = Populate(db);
    db.SetColumns(Collection, PersonColumns().Where(column => column.Name != "Passport"),
      [ColumnMigration.Remove(0, "Passport")]);
    db.Rewrite(Collection);
    var people = db.Entities<Person>();
    var kept = db.Versions.Head(Collection, after[0]);
    Assert.True(kept.Image.Values.ContainsKey("Passport"));

    people.Update(after[0], new Person { Id = 10, Name = "Person-10", Age = 77, Tags = [] });

    var again = db.Versions.Node(Collection, after[0], kept.VersionId);
    Assert.True(again.Image.Values.ContainsKey("Passport"), "the earlier, more complete image was replaced");
    Assert.True(db.Versions.Verify(Collection).IsSound);
  }

  //The in-place paths: the system store refuses a user collection outright, and MigrateRow is
  //reachable only through Rewrite (the call-site test), which is the rule above.
  [Fact]
  public void TheSystemDocumentStoreCannotWriteAUserCollection() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    Populate(db);
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(Ulid.NewUlid()));
    document.SetValue(new ObjectDocumentValue());
    Assert.Throws<ArgumentException>(() => db.InTransaction(() => db.SystemDocuments.Write(Collection, Ulid.NewUlid(), document)));
    Assert.Throws<ArgumentException>(() => db.InTransaction(() => db.SystemDocuments.Delete(Collection, Ulid.NewUlid())));
  }

  [Fact]
  public void VerifyFindsWhatItShouldAndNothingElse() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    var (before, after) = Populate(db);
    var people = db.Entities<Person>();
    people.Delete(after[0]);
    people.Update(before[0], TestPeople.Numbered(30));
    var sound = db.Versions.Verify(Collection);
    Assert.True(sound.IsSound, sound.ToString());
    //Four records seen: three live, one deleted with history; before[1] has no history yet.
    Assert.Equal(4, sound.Records);
    Assert.Equal(6, sound.Nodes);
    Assert.True(sound.IndexEntries >= sound.Nodes);

    //An unversioned collection has nothing to verify and says so.
    db.CreateCollection("Other", PersonColumns());
    Assert.True(db.Versions.Verify("Other").IsSound);
  }
}
