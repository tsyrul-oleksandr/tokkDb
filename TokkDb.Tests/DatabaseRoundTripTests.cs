using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;
using Xunit;

namespace TokkDb.Tests;

public class DatabaseRoundTripTests {
  //The root page, the catalogue's own pages and the free-space structures all precede the
  //first page of user data, so what counts as overhead is read rather than assumed.
  private static long ReservedPages(TempDatabaseFile file, TokkDbConnection db) {
    return db.Collection("Person").DataFirstPage;
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    return db;
  }

  [Fact]
  public void ANewDatabaseIsEmptyAndReportsItself() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    Assert.False(db.IsExists());
    db.CreateDatabase(config => config.CreateEntity<Person>());
    Assert.True(db.IsExists());
    Assert.Empty(db.Entities<Person>().GetAll());
  }

  [Fact]
  public void InsertedRecordsComeBackIntact() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    entities.Insert(TestPeople.Ivan());

    var person = Assert.Single(entities.GetAll());
    Assert.Equal(1, person.Id);
    Assert.Equal("Ivan", person.Name);
    Assert.Equal(29, person.Age);
    Assert.Equal("ST-111111", person.Passport.Code);
    Assert.Equal(["tag1", "tag2"], person.Tags.Select(tag => tag.Name));
  }

  [Fact]
  public void RecordsSurviveReopeningTheFile() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      db.Entities<Person>().Insert(TestPeople.Ivan());
    }

    using var reopened = new TokkDbConnection(file.Path);
    Assert.True(reopened.IsExists());
    reopened.Load();

    var person = Assert.Single(reopened.Entities<Person>().GetAll());
    Assert.Equal("Ivan", person.Name);
    Assert.Equal("ST-111111", person.Passport.Code);
  }

  [Theory]
  [InlineData(59)]
  [InlineData(60)]
  [InlineData(500)]
  public void RecordsSpanningManyPagesAllComeBack(int count) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    for (var i = 0; i < count; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    var all = entities.GetAll().OrderBy(person => person.Id).ToList();
    Assert.Equal(count, all.Count);
    Assert.Equal(Enumerable.Range(0, count), all.Select(person => person.Id));
    Assert.All(all, person => {
      Assert.Equal($"Person-{person.Id}", person.Name);
      Assert.Equal($"ST-{person.Id:D6}", person.Passport.Code);
      Assert.Equal($"tag-{person.Id}", Assert.Single(person.Tags).Name);
    });
  }

  [Fact]
  public void TheDataPageChainIsFollowedAcrossReopens() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      var entities = db.Entities<Person>();
      for (var i = 0; i < 500; i++) {
        entities.Insert(TestPeople.Numbered(i));
      }
      Assert.True(file.PageCount > ReservedPages(file, db) + 1,
        $"expected the data to span several pages, got {file.PageCount}");
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(500, reopened.Entities<Person>().GetAll().Count());
  }

  //VR-11 says the flags byte is read in this pass. Nothing marks an image dead yet, so the
  //only way to show that a scan honours it is to write one by hand.
  [Fact]
  public void AnImageThatIsNotLiveIsSkippedByAScan() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      db.Entities<Person>().Insert(TestPeople.Ivan());
    }

    AppendSupersededImage(file, TestPeople.Numbered(2));

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    //Both images are on the page; only the live one comes back.
    Assert.Equal("Ivan", Assert.Single(reopened.Entities<Person>().GetAll()).Name);
  }

  private static void AppendSupersededImage(TempDatabaseFile file, Person person) {
    using var disk = new DiskManager(file.Path);
    disk.SetPageSize(RootPage.ReadPrefix(disk.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var pageManager = new PageManager(disk);
    var transactions = new TransactionManager(pageManager);
    var rootPageManager = new RootPageManager(pageManager, transactions);
    var catalog = new CollectionCatalog(rootPageManager, transactions);
    var freeSpace = new FreeSpaceManager(pageManager, rootPageManager, catalog, transactions);
    var dataPageManager = new DataPageManager(pageManager, catalog, freeSpace, transactions);
    catalog.SetDataPageManager(dataPageManager);

    var transaction = transactions.CreateTransaction();
    rootPageManager.Initialize();
    catalog.Initialize();

    var recordId = Ulid.NewUlid();
    var document = new DocumentSerializer<Person>().Create(person, recordId);
    var header = RecordHeader.ForNewRecord(recordId);
    header.Flags = RecordFlags.Superseded;
    dataPageManager.WriteRecord("Person", header, document);
    transaction.Commit();
  }

  [Fact]
  public void PagesFillUpBeforeANewOneIsAllocated() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    for (var i = 0; i < 200; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    // 200 records of ~130 bytes must not need more than one page each 8KB of payload. The
    // primary index costs pages of its own (DC-4), counted here rather than left to slack.
    var indexPages = db.PrimaryIndex("Person").Nodes().Count();
    var dataPages = file.PageCount - ReservedPages(file, db) - indexPages;
    Assert.InRange(dataPages, 1, 200 * 200 / TokkConstants.DefaultPageSize + 2);
    Assert.Equal(1, indexPages);
  }
}
