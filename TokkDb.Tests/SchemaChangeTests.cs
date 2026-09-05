using TokkDb.Pages;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//DC-7 and D-4. Changing a schema after records exist, dropping a collection, and the two
//things a collection carries that are not its structure.
public class SchemaChangeTests {
  private const string Collection = nameof(Person);

  private static List<ColumnDescriptor> PersonColumns() {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ];
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, PersonColumns());
    return db;
  }

  private static void Fill(TokkDbConnection db, int count) {
    var entities = db.Entities<Person>(Collection);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        entities.Insert(TestPeople.Numbered(i));
      }
    });
  }

  [Fact]
  public void AddingAColumnBumpsTheSchemaVersionAndLeavesTheRecordsAlone() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 50);
    var before = db.Collection(Collection).SchemaVersion;

    var columns = PersonColumns();
    columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
    db.SetColumns(Collection, columns);

    Assert.Equal(before + 1, db.Collection(Collection).SchemaVersion);
    Assert.Contains(db.Collection(Collection).Columns, column => column.Name == "City");
    //VR-11: the records keep the version they were written under, which is what makes the
    //migration lazy rather than a rewrite of the collection.
    Assert.Equal(50, db.Entities<Person>(Collection).GetAll().Count());
  }

  //An index over a column that no longer exists could never be chosen by the planner and
  //would still be maintained on every write.
  [Fact]
  public void DroppingAColumnDropsTheIndexOverIt() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Age");
    Fill(db, 50);
    Assert.Contains(db.Indexes, index => index.ColumnName == "Age");

    db.SetColumns(Collection, PersonColumns().Where(column => column.Name != "Age"));

    Assert.DoesNotContain(db.Indexes, index => index.ColumnName == "Age");
    Assert.DoesNotContain(db.Collection(Collection).SecondaryIndexRoots.Keys, name => name == "Age");
  }

  //A unique index that outlived the declaration would go on refusing duplicates the schema
  //now permits.
  [Fact]
  public void AColumnThatStopsBeingUniqueLosesItsUniqueIndex() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 10);
    Assert.Contains(db.Indexes, index => index is { ColumnName: "Name", Unique: true });

    db.SetColumns(Collection, PersonColumns()
      .Select(column => column.Name == "Name"
        ? new ColumnDescriptor("Name", ValueTypeEnum.String)
        : column));

    Assert.DoesNotContain(db.Indexes, index => index is { ColumnName: "Name", Unique: true });
    var entities = db.Entities<Person>(Collection);
    var duplicate = TestPeople.Numbered(1);
    duplicate.Id = 99;
    entities.Insert(duplicate);
    Assert.Equal(11, entities.GetAll().Count());
  }

  [Fact]
  public void ADroppedIndexReleasesItsPagesForTheNextOne() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Age");
    Fill(db, 5_000);
    var withTheIndex = file.PageCount;

    Assert.True(db.DropIndex(Collection, "Age"));
    Assert.False(db.DropIndex(Collection, "Age"));
    //Built again over the same values: it takes the pages the dropped one gave back rather
    //than growing the file.
    db.CreateIndex(Collection, "Age");

    Assert.Equal(withTheIndex, file.PageCount);
    Assert.Equal(125, db.Entities<Person>(Collection).GetBy("Age", 30).Count());
  }

  [Fact]
  public void ADroppedCollectionIsGoneAfterAReopen() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      db.CreateIndex(Collection, "Age");
      db.SetMetadata(Collection, new Dictionary<string, string> { ["source"] = "crm" });
      db.SetDisplayRule(Collection, "{Name}");
      Fill(db, 200);

      Assert.True(db.DropCollection(Collection));
      Assert.False(db.DropCollection(Collection));
      Assert.DoesNotContain(db.Collections, collection => collection.Name == Collection);
      Assert.Empty(db.Indexes);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.DoesNotContain(reopened.Collections, collection => collection.Name == Collection);
    Assert.Empty(reopened.Indexes);
    //Both of the collection's own documents went with it, so nothing describes a collection
    //that is not there.
    Assert.Empty(reopened.Metadata(Collection));
    Assert.Null(reopened.DisplayRule(Collection));
  }

  [Fact]
  public void ACollectionCanBeCreatedAgainUnderTheNameOfADroppedOne() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 100);
    db.DropCollection(Collection);

    db.CreateCollection(Collection, PersonColumns());

    //A new collection, not the old one reappearing: the records of the old one are not
    //reachable through it, because it has a data chain of its own.
    Assert.Empty(db.Entities<Person>(Collection).GetAll());
  }

  //Identifiers are never reused, so a page left behind by the dropped collection can never be
  //mistaken for a page of the new one.
  [Fact]
  public void ANewCollectionNeverReusesTheOwningIdOfADroppedOne() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var original = db.Collection(Collection).OwningCollectionId;
    db.DropCollection(Collection);

    db.CreateCollection(Collection, PersonColumns());

    Assert.NotEqual(original, db.Collection(Collection).OwningCollectionId);
  }

  [Fact]
  public void ADroppedCollectionTakesTheRelationsThatNamedItWithIt() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true)]);
    db.CreateRelation("PersonCity", Collection, "Name", "City", "Name");
    Assert.Single(db.Relations);

    db.DropCollection("City");

    Assert.Empty(db.Relations);
  }

  [Fact]
  public void ARemovedRelationStopsBeingChecked() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true)]);
    db.CreateRelation("PersonCity", Collection, "Name", "City", "Name");
    var entities = db.Entities<Person>(Collection);
    Assert.Throws<TokkDb.Pages.Relations.ReferentialIntegrityException>(
      () => entities.Insert(TestPeople.Numbered(1)));

    Assert.True(db.RemoveRelation("PersonCity"));
    Assert.False(db.RemoveRelation("PersonCity"));

    entities.Insert(TestPeople.Numbered(1));
    Assert.Single(entities.GetAll());
  }

  //D-4: both are documents in their own system collections, so they survive a reopen without
  //being part of the structural descriptor.
  [Fact]
  public void TheDisplayRuleAndTheMetadataSurviveAReopen() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      db.SetDisplayRule(Collection, "{Name} ({Age})");
      db.SetMetadata(Collection, new Dictionary<string, string> {
        ["source"] = "crm", ["Ключ"] = "значення"
      });
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();

    Assert.Equal("{Name} ({Age})", reopened.DisplayRule(Collection));
    Assert.Equal("crm", reopened.Metadata(Collection)["source"]);
    Assert.Equal("значення", reopened.Metadata(Collection)["Ключ"]);
  }

  [Fact]
  public void ADisplayRuleIsReplacedAndCleared() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);

    db.SetDisplayRule(Collection, "{Name}");
    db.SetDisplayRule(Collection, "{Name} ({Age})");
    Assert.Equal("{Name} ({Age})", db.DisplayRule(Collection));

    db.SetDisplayRule(Collection, null);
    Assert.Null(db.DisplayRule(Collection));
  }

  //A settings document grows as entries are added, past the slot it was first written into.
  [Fact]
  public void MetadataThatOutgrowsItsSlotIsRewrittenRatherThanRefused() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      db.SetMetadata(Collection, new Dictionary<string, string> { ["one"] = "1" });
      db.SetMetadata(Collection, Enumerable.Range(0, 30)
        .ToDictionary(i => $"key-{i:D3}", i => new string('v', 100)));
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();

    var metadata = reopened.Metadata(Collection);
    Assert.Equal(30, metadata.Count);
    Assert.Equal(new string('v', 100), metadata["key-012"]);
    //Replaced rather than merged: the settings of a collection are one document.
    Assert.False(metadata.ContainsKey("one"));
  }

  //The limit that comes with keeping the settings in one document: it has to fit a page.
  //Growing a record into an overflow chain is ST-6 and not implemented, so a settings map
  //past about 8 KB is refused with the storage layer's own error rather than silently losing
  //entries. Recorded as a test because it is a boundary a caller can hit, not an accident.
  [Fact]
  public void MetadataLargerThanAPageIsRefusedRatherThanTruncated() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.SetMetadata(Collection, new Dictionary<string, string> { ["one"] = "1" });

    Assert.Throws<PageOverflowException>(() => db.SetMetadata(Collection, Enumerable.Range(0, 100)
      .ToDictionary(i => $"key-{i:D3}", i => new string('v', 100))));

    //The transaction rolled back, so what was there is still there.
    Assert.Equal("1", db.Metadata(Collection)["one"]);
  }

  //The system collections describe their own columns, for the same reason _collections does:
  //nothing about the catalogue should be readable only in code (DC-7).
  [Fact]
  public void TheSystemCollectionsThatHoldDescriptorsDescribeTheirOwnColumns() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);

    foreach (var name in new[] {
      SystemCollections.Collections, SystemCollections.Indexes, SystemCollections.Relations,
      SystemCollections.DisplayRules, SystemCollections.Settings
    }) {
      Assert.NotEmpty(db.Collection(name).Columns);
    }
  }
}
