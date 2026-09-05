using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Relations;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//DC-4: secondary indexes over the composite key of D-3, the unique constraint they enforce,
//and the referential check they are what makes affordable.
public class SecondaryIndexTests {
  private const string Collection = nameof(Person);

  private readonly ITestOutputHelper _output;

  public SecondaryIndexTests(ITestOutputHelper output) {
    _output = output;
  }

  //Explicit columns rather than reflection, because Name is declared unique and that
  //declaration is what creates the unique index.
  private static List<ColumnDescriptor> PersonColumns(bool uniqueName = false) {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String, unique: uniqueName),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ];
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, bool uniqueName = false) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, PersonColumns(uniqueName));
    return db;
  }

  private static List<Ulid> Fill(TokkDbConnection db, int count, Func<int, Person> make = null) {
    var entities = db.Entities<Person>(Collection);
    var ids = new List<Ulid>(count);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        ids.Add(entities.Insert((make ?? TestPeople.Numbered)(i)));
      }
    });
    return ids;
  }

  [Fact]
  public void AnIndexedColumnIsFoundThroughItsIndexRatherThanByScanning() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Name");
    Fill(db, 20_000);

    var entities = db.Entities<Person>(Collection);
    var pagesInTheFile = file.PageCount;
    var readsBefore = db.PageReadCount;
    var found = entities.GetBy("Name", "Person-12345").ToList();
    var pagesRead = db.PageReadCount - readsBefore;

    _output.WriteLine($"{pagesInTheFile:N0} pages in the file, {pagesRead} read for one lookup by Name");
    Assert.Single(found);
    Assert.Equal(12_345, found[0].Value.Id);
    //A descent of the index and the data page the entry addresses — not the collection.
    Assert.InRange(pagesRead, 1, 5);
    Assert.True(pagesRead * 20 < pagesInTheFile, $"{pagesRead} pages is no saving over {pagesInTheFile}");
  }

  //D-3's reason for the composite key: the identity makes every entry distinct, so a column
  //with the same value on many records needs no list of records hanging off that value.
  [Fact]
  public void ARepeatedValueReturnsEveryRecordCarryingItWithNoPostingList() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Age");
    //Age is 20 + i % 40, so forty distinct values over two thousand records.
    Fill(db, 2_000);

    var entities = db.Entities<Person>(Collection);
    var twentyNine = entities.GetBy("Age", 29).ToList();
    Assert.Equal(50, twentyNine.Count);
    Assert.All(twentyNine, record => Assert.Equal(29, record.Value.Age));
    //Every record is under exactly one of the forty values.
    Assert.Equal(2_000, Enumerable.Range(20, 40).Sum(age => entities.GetBy("Age", age).Count()));
    Assert.Empty(entities.GetBy("Age", 999));
  }

  [Fact]
  public void AUniqueColumnRefusesASecondRecordHoldingTheSameValue() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, uniqueName: true);
    var entities = db.Entities<Person>(Collection);
    var first = entities.Insert(TestPeople.Numbered(1));

    var thrown = Assert.Throws<UniqueConstraintViolationException>(() =>
      entities.Insert(TestPeople.Numbered(1)));

    //Named: which column, and which record already holds it.
    Assert.Equal("Name", thrown.ColumnName);
    Assert.Equal(Collection, thrown.CollectionName);
    Assert.Equal(first, thrown.ConflictingRecordId);
    Assert.Equal("Person-1", thrown.Value);
    Assert.Contains("Person-1", thrown.Message);
    Assert.Contains(first.ToString(), thrown.Message);

    //And the refusal left nothing behind.
    Assert.Single(entities.GetAll());
    Assert.Single(entities.GetBy("Name", "Person-1"));
  }

  [Fact]
  public void AUniqueColumnLetsARecordKeepTheValueItAlreadyHas() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, uniqueName: true);
    var entities = db.Entities<Person>(Collection);
    var recordId = entities.Insert(TestPeople.Numbered(1));

    //Same name, different age: the conflict is with itself and is not one.
    entities.Update(recordId, new Person {
      Id = 1, Name = "Person-1", Age = 44, Passport = new Passport("ST-000001"), Tags = []
    });

    Assert.Equal(44, entities.GetById(recordId).Value.Age);
    Assert.Single(entities.GetBy("Name", "Person-1"));
  }

  //A column may be unique and still optional. If null conflicted with null, every record
  //missing the value would collide with every other one.
  [Fact]
  public void AUniqueColumnDoesNotMakeTwoMissingValuesAConflict() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, uniqueName: true);
    var entities = db.Entities<Person>(Collection);

    var first = entities.Insert(new Person { Id = 1, Name = null, Age = 20, Tags = [] });
    var second = entities.Insert(new Person { Id = 2, Name = null, Age = 21, Tags = [] });

    Assert.NotEqual(first, second);
    Assert.Equal(2, entities.GetAll().Count());
    Assert.Equal(2, entities.GetBy("Name", null).Count());
  }

  //An index created over a collection that already holds records reads them once, and after
  //that the collection is never read to find out what the index contains.
  [Fact]
  public void AnIndexOverANonEmptyCollectionFindsWhatWasAlreadyThere() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 1_000);

    db.CreateIndex(Collection, "Name");

    var entities = db.Entities<Person>(Collection);
    Assert.Single(entities.GetBy("Name", "Person-0"));
    Assert.Single(entities.GetBy("Name", "Person-999"));
    Assert.Empty(entities.GetBy("Name", "Person-1000"));
  }

  //Building a unique index over records that already break it has to say so rather than
  //quietly keeping one of them.
  [Fact]
  public void AUniqueIndexOverACollectionThatAlreadyBreaksItIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>(Collection);
    entities.Insert(TestPeople.Numbered(1));
    entities.Insert(TestPeople.Numbered(1));

    var thrown = Assert.Throws<UniqueConstraintViolationException>(() =>
      db.CreateIndex(Collection, "Name", unique: true));
    Assert.Equal("Name", thrown.ColumnName);
  }

  [Fact]
  public void AnUpdateMovesTheEntryFromTheOldValueToTheNewOne() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Name");
    var entities = db.Entities<Person>(Collection);
    var recordId = entities.Insert(TestPeople.Numbered(7));

    entities.Update(recordId, new Person {
      Id = 7, Name = "Олександр", Age = 29, Passport = new Passport("ST-000007"), Tags = []
    });

    Assert.Empty(entities.GetBy("Name", "Person-7"));
    Assert.Single(entities.GetBy("Name", "Олександр"));
    Assert.Equal(recordId, entities.GetBy("Name", "Олександр").Single().RecordId);
  }

  [Fact]
  public void ADeleteTakesTheIndexEntriesWithTheRecord() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Name");
    db.CreateIndex(Collection, "Age");
    var ids = Fill(db, 200);
    var entities = db.Entities<Person>(Collection);

    entities.Delete(ids[50]);

    Assert.Empty(entities.GetBy("Name", "Person-50"));
    Assert.Equal(4, entities.GetBy("Age", 20 + 50 % 40).Count());
    Assert.Equal(199, entities.GetAll().Count());
  }

  [Fact]
  public void TheIndexesAreReadBackFromTheFileAtOpen() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file, uniqueName: true)) {
      db.CreateIndex(Collection, "Age");
      Fill(db, 500);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(2, reopened.Indexes.Count());
    Assert.Contains(reopened.Indexes, index => index.ColumnName == "Name" && index.Unique);
    Assert.Contains(reopened.Indexes, index => index.ColumnName == "Age" && !index.Unique);

    var entities = reopened.Entities<Person>(Collection);
    Assert.Single(entities.GetBy("Name", "Person-250"));
    Assert.Equal(13, entities.GetBy("Age", 29).Count());
    //And it is still enforced after the reopen.
    Assert.Throws<UniqueConstraintViolationException>(() => entities.Insert(TestPeople.Numbered(250)));
  }

  //An unindexed column has no lookup, only the scan Get already is. Saying so is better than
  //quietly giving the caller the slow one under the fast one's name.
  [Fact]
  public void LookingUpAColumnWithNoIndexSaysSoRatherThanScanning() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 10);
    var thrown = Assert.Throws<InvalidOperationException>(() =>
      db.Entities<Person>(Collection).GetBy("Age", 20).ToList());
    Assert.Contains("no index", thrown.Message);
  }

  //An object and an array have no ordering, so there is no key an index over one could be
  //sorted by. Refused when the index is defined rather than when the first record is written.
  [Fact]
  public void AColumnWithNoOrderingCannotBeIndexed() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Assert.Throws<ArgumentException>(() => db.CreateIndex(Collection, "Passport"));
    Assert.Throws<ArgumentException>(() => db.CreateIndex(Collection, "Tags"));
    Assert.Throws<ArgumentException>(() => db.CreateIndex(Collection, "Nonexistent"));
  }

  [Fact]
  public void IndexingTheSameColumnTwiceIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    db.CreateIndex(Collection, "Name");
    Assert.Throws<ArgumentException>(() => db.CreateIndex(Collection, "Name"));
  }

  private const string Target = "Author";

  private static TokkDbConnection NewRelatedDatabase(TempDatabaseFile file) {
    var db = NewDatabase(file);
    db.CreateCollection(Target, PersonColumns());
    return db;
  }

  //DC-4: the check a relation describes is a lookup by value on the target column, and a
  //lookup by value is what an index is. So the index is not an optimisation of the
  //constraint, it is the constraint's only affordable implementation — and creating the
  //relation is what puts it there.
  [Fact]
  public void CreatingARelationCreatesTheIndexItsCheckNeeds() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    Assert.Empty(db.Indexes);

    db.CreateRelation("person_author", Collection, "Name", Target, "Name");

    var index = Assert.Single(db.Indexes);
    Assert.Equal(Target, index.CollectionName);
    Assert.Equal("Name", index.ColumnName);
  }

  [Fact]
  public void ARelationOverAnAlreadyIndexedTargetUsesTheIndexThatIsThere() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    db.CreateIndex(Target, "Name");

    db.CreateRelation("person_author", Collection, "Name", Target, "Name");

    Assert.Single(db.Indexes);
  }

  [Fact]
  public void AWriteWhoseTargetIsNotThereIsRefusedAndNamed() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    db.CreateRelation("person_author", Collection, "Name", Target, "Name");
    db.Entities<Person>(Target).Insert(TestPeople.Numbered(1));

    var thrown = Assert.Throws<ReferentialIntegrityException>(() =>
      db.Entities<Person>(Collection).Insert(TestPeople.Numbered(2)));

    Assert.Equal("person_author", thrown.Relation.Name);
    Assert.Equal("Person-2", thrown.Value);
    Assert.Contains("Person-2", thrown.Message);
    Assert.Contains(Target, thrown.Message);
    Assert.Empty(db.Entities<Person>(Collection).GetAll());
  }

  [Fact]
  public void AWriteWhoseTargetIsThereIsAccepted() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    db.CreateRelation("person_author", Collection, "Name", Target, "Name");
    db.Entities<Person>(Target).Insert(TestPeople.Numbered(1));

    var recordId = db.Entities<Person>(Collection).Insert(TestPeople.Numbered(1));

    Assert.Equal("Person-1", db.Entities<Person>(Collection).GetById(recordId).Value.Name);
  }

  //A column that refers to nothing is not a broken reference — an optional relationship is
  //still a relationship.
  [Fact]
  public void AReferenceThatIsNullIsNotABrokenOne() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    db.CreateRelation("person_author", Collection, "Name", Target, "Name");

    var recordId = db.Entities<Person>(Collection)
      .Insert(new Person { Id = 1, Name = null, Age = 20, Tags = [] });

    Assert.NotNull(db.Entities<Person>(Collection).GetById(recordId));
  }

  //An update is a write, so it is checked like one: a record cannot be changed into a broken
  //reference any more than it can be created as one.
  [Fact]
  public void AnUpdateIntoABrokenReferenceIsRefusedAndLeavesTheRecordAsItWas() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    db.CreateRelation("person_author", Collection, "Name", Target, "Name");
    db.Entities<Person>(Target).Insert(TestPeople.Numbered(1));
    var entities = db.Entities<Person>(Collection);
    var recordId = entities.Insert(TestPeople.Numbered(1));

    Assert.Throws<ReferentialIntegrityException>(() =>
      entities.Update(recordId, TestPeople.Numbered(9)));

    Assert.Equal("Person-1", entities.GetById(recordId).Value.Name);
    Assert.Single(entities.GetAll());
  }

  [Fact]
  public void TheRelationsAreReadBackFromTheFileAtOpen() {
    using var file = new TempDatabaseFile();
    using (var db = NewRelatedDatabase(file)) {
      db.CreateRelation("person_author", Collection, "Name", Target, "Name");
      db.Entities<Person>(Target).Insert(TestPeople.Numbered(1));
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var relation = Assert.Single(reopened.Relations);
    Assert.Equal(Collection, relation.SourceCollection);
    Assert.Equal(Target, relation.TargetCollection);
    //And it is still enforced.
    Assert.Throws<ReferentialIntegrityException>(() =>
      reopened.Entities<Person>(Collection).Insert(TestPeople.Numbered(2)));
    reopened.Entities<Person>(Collection).Insert(TestPeople.Numbered(1));
  }

  [Fact]
  public void ARelationOverAColumnThatDoesNotExistIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    Assert.Throws<ArgumentException>(() =>
      db.CreateRelation("bad", Collection, "Nonexistent", Target, "Name"));
    Assert.Throws<ArgumentException>(() =>
      db.CreateRelation("bad", Collection, "Name", Target, "Nonexistent"));
    Assert.Empty(db.Relations);
  }
}
