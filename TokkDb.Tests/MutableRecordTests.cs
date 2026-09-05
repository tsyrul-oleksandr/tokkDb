using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Tests;

//VR-12: copy-on-write update, delete through one seam, under RetentionPolicy.None.
public class MutableRecordTests {
  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    return db;
  }

  [Fact]
  public void InsertUpdateAndDeleteRoundTrip() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      var entities = db.Entities<Person>();
      entities.Insert(TestPeople.Ivan());
      entities.Insert(TestPeople.Numbered(2));

      var ivan = entities.GetAllRecords().Single(record => record.Value.Name == "Ivan");
      entities.Update(ivan.RecordId, new Person {
        Id = 1, Name = "Ivan Updated", Age = 30,
        Passport = new Passport("ST-999999"), Tags = [new Tag("changed")]
      });

      var second = entities.GetAllRecords().Single(record => record.Value.Id == 2);
      entities.Delete(second.RecordId);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var person = Assert.Single(reopened.Entities<Person>().GetAll());
    Assert.Equal("Ivan Updated", person.Name);
    Assert.Equal(30, person.Age);
    Assert.Equal("ST-999999", person.Passport.Code);
    Assert.Equal(["changed"], person.Tags.Select(tag => tag.Name));
    Assert.Equal(1u, reopened.Collection("Person").RecordCount);
  }

  [Fact]
  public void AnUpdateKeepsTheRecordIdentityAndReplacesTheImage() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    entities.Insert(TestPeople.Ivan());

    var before = Assert.Single(entities.GetAllRecords());
    entities.Update(before.RecordId, TestPeople.Numbered(5));
    var after = Assert.Single(entities.GetAllRecords());

    //D-1: one identity for the life of the record, however many images it has had.
    Assert.Equal(before.RecordId, after.RecordId);
    Assert.Equal("Person-5", after.Value.Name);
    //Exactly one live image, so the scan cannot return the record twice.
    Assert.Single(entities.GetAll());
    Assert.Equal(1u, db.Collection("Person").RecordCount);
  }

  [Fact]
  public void AnUpdateDoesNotRewriteTheOldImageInPlace() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    entities.Insert(TestPeople.Ivan());
    var record = Assert.Single(entities.GetAllRecords());

    var versionBefore = Assert.Single(ReadAllImages(file, "Person")).VersionId;

    entities.Update(record.RecordId, TestPeople.Numbered(9));

    //A new image, not the old one rewritten where it lay: same record, different version.
    var images = ReadAllImages(file, "Person");
    var live = Assert.Single(images);
    Assert.Equal(record.RecordId, live.RecordId);
    Assert.NotEqual(versionBefore, live.VersionId);
    Assert.Equal(RecordFlags.Live, live.Flags);
    //Under RetentionPolicy.None the retired image is gone rather than kept beside it.
    Assert.DoesNotContain(images, image => image.Flags.HasFlag(RecordFlags.Superseded));
  }

  [Fact]
  public void ADeletedRecordIsGoneAndItsSpaceIsBackOnTheFreeList() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    for (var i = 0; i < 5; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }
    var freeListBefore = ReadFreeListBytes(file, "Person");

    var doomed = entities.GetAllRecords().Single(record => record.Value.Id == 2);
    entities.Delete(doomed.RecordId);

    Assert.Equal(4, entities.GetAll().Count());
    Assert.DoesNotContain(entities.GetAll(), person => person.Id == 2);
    Assert.Equal(4u, db.Collection("Person").RecordCount);
    Assert.True(ReadFreeListBytes(file, "Person") > freeListBefore,
      "the retired image's bytes did not return to the free list");
  }

  [Fact]
  public void FreedSpaceIsHandedOutAgain() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    for (var i = 0; i < 20; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }
    var pagesBefore = file.PageCount;

    //Update every record: each new image can take the slot its predecessor gave up.
    foreach (var record in entities.GetAllRecords().ToList()) {
      entities.Update(record.RecordId, TestPeople.Numbered(record.Value.Id));
    }

    Assert.Equal(20, entities.GetAll().Count());
    Assert.Equal(pagesBefore, file.PageCount);
  }

  [Fact]
  public void UpdatingOrDeletingAnUnknownRecordIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    entities.Insert(TestPeople.Ivan());
    var missing = Ulid.NewUlid();

    Assert.Throws<RecordNotFoundException>(() => entities.Update(missing, TestPeople.Numbered(3)));
    Assert.Throws<RecordNotFoundException>(() => entities.Delete(missing));
    //And the refusal changed nothing.
    Assert.Equal("Ivan", Assert.Single(entities.GetAll()).Name);
    Assert.Equal(1u, db.Collection("Person").RecordCount);
  }

  [Fact]
  public void KeepVersionsIsDeclaredAndRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    entities.Insert(TestPeople.Ivan());
    var record = Assert.Single(entities.GetAllRecords());

    entities.RetentionPolicy = RetentionPolicy.KeepVersions;

    var update = Assert.Throws<NotSupportedException>(() => entities.Update(record.RecordId, TestPeople.Numbered(4)));
    Assert.Contains(nameof(RetentionPolicy.KeepVersions), update.Message);
    Assert.Throws<NotSupportedException>(() => entities.Delete(record.RecordId));

    //The refusal is the whole of it: nothing was retired on the way to throwing.
    entities.RetentionPolicy = RetentionPolicy.None;
    Assert.Equal("Ivan", Assert.Single(entities.GetAll()).Name);
  }

  //VR-12's acceptance criterion, with the fault injector standing in for the kill.
  [Fact]
  public void ACrashDuringAnUpdateLeavesTheOldRecordReadable() {
    using var file = new TempDatabaseFile();
    Ulid recordId;
    using (var db = NewDatabase(file)) {
      var entities = db.Entities<Person>();
      entities.Insert(TestPeople.Ivan());
      recordId = Assert.Single(entities.GetAllRecords()).RecordId;
    }
    var databaseBeforeTheCrash = File.ReadAllBytes(file.Path);

    var writesInACleanUpdate = CountWritesInAnUpdate(file, recordId);
    for (var killAfter = 1; killAfter <= writesInACleanUpdate; killAfter++) {
      RestoreDatabase(file, databaseBeforeTheCrash);
      KillDuringUpdate(file, recordId, killAfter);

      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var person = Assert.Single(reopened.Entities<Person>().GetAll());
      //Either the update landed whole or it did not land at all. Never a lost record, never
      //two images of one.
      Assert.True(person.Name is "Ivan" or "Person-77",
        $"kill after write {killAfter} left '{person.Name}'");
      Assert.Equal(1u, reopened.Collection("Person").RecordCount);
    }
  }

  private static void KillDuringUpdate(TempDatabaseFile file, Ulid recordId, int killAfterWrites) {
    var disk = new FaultInjectingDiskManager(file.Path, killAfterWrites);
    var db = new TokkDbConnection(disk);
    try {
      db.Load();
      db.Entities<Person>().Update(recordId, Person77());
    } catch (SimulatedProcessKillException) {
      //A killed process gets no further than this.
    } finally {
      db.Dispose();
    }
  }

  private static int CountWritesInAnUpdate(TempDatabaseFile file, Ulid recordId) {
    var snapshot = File.ReadAllBytes(file.Path);
    var disk = new FaultInjectingDiskManager(file.Path);
    using (var db = new TokkDbConnection(disk)) {
      db.Load();
      db.Entities<Person>().Update(recordId, Person77());
    }
    RestoreDatabase(file, snapshot);
    return disk.WriteCount;
  }

  private static Person Person77() {
    return new Person {
      Id = 77, Name = "Person-77", Age = 40,
      Passport = new Passport("ST-000077"), Tags = [new Tag("tag-77")]
    };
  }

  private static void RestoreDatabase(TempDatabaseFile file, byte[] contents) {
    File.WriteAllBytes(file.Path, contents);
    var journal = Journal.GetJournalPath(file.Path);
    if (File.Exists(journal)) {
      File.WriteAllBytes(journal, []);
    }
  }

  private static IReadOnlyList<RecordHeader> ReadAllImages(TempDatabaseFile file, string collectionName) {
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var rootPage = pageManager.LoadPage<RootPage>(Configuration.TokkConstants.RootPageIndex);

    var headers = new List<RecordHeader>();
    foreach (var page in DataPages(pageManager, rootPage.CollectionsFirstPageId, collectionName)) {
      headers.AddRange(page.GetItems().Select(StoredRecordUtilities.ReadHeader));
    }
    return headers;
  }

  private static ushort ReadFreeListBytes(TempDatabaseFile file, string collectionName) {
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var rootPage = pageManager.LoadPage<RootPage>(Configuration.TokkConstants.RootPageIndex);
    return (ushort)DataPages(pageManager, rootPage.CollectionsFirstPageId, collectionName)
      .Sum(page => page.FreeListBytes);
  }

  private static IEnumerable<DataPage> DataPages(PageManager pageManager, uint cataloguePage,
      string collectionName) {
    var first = FindDataFirstPage(pageManager, cataloguePage, collectionName);
    var next = first;
    while (next != default) {
      var page = pageManager.LoadPage<DataPage>(next);
      yield return page;
      next = page.NextPageIndex;
    }
  }

  private static uint FindDataFirstPage(PageManager pageManager, uint cataloguePage, string collectionName) {
    var next = cataloguePage;
    while (next != default) {
      var page = pageManager.LoadPage<DataPage>(next);
      foreach (var item in page.GetItems()) {
        var descriptor = CollectionDescriptorDocument.Read(StoredRecordUtilities.FromBuffer(item).Document);
        if (descriptor.Name == collectionName) {
          return descriptor.DataFirstPage;
        }
      }
      next = page.NextPageIndex;
    }
    return default;
  }
}
