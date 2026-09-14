using TokkDb.Disk;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//The tests that stand in for a clock that steps back or a process that restarts touch the
//engine's one clock and its one identifier source, which every other test uses too. They run
//alone, after or before everything else, never beside it.
[CollectionDefinition(Name, DisableParallelization = true)]
public class EngineClockCollection {
  public const string Name = "Engine clock";
}

//HS-7 and V-8: one monotonic source for every identifier, seeded from a durable mark.
[Collection(EngineClockCollection.Name)]
public class IdentifierTests {
  [Fact]
  public void TenThousandIdentifiersMintedInATightLoopAscendStrictly() {
    var previous = RecordIdentity.Next();
    for (var i = 0; i < 10_000; i++) {
      var next = RecordIdentity.Next();
      Assert.True(next.CompareTo(previous) > 0, $"identifier {i} did not ascend");
      previous = next;
    }
  }

  [Fact]
  public void WithAClockThatStepsBackIdentifiersStillAscendAndLogicalTimeIsAtLeastRecordedTime() {
    var start = DateTimeOffset.UtcNow;
    //Forward, then back an hour, then forward again in small steps, then back a day.
    var readings = new List<DateTimeOffset>();
    for (var i = 0; i < 50; i++) {
      readings.Add(start.AddMilliseconds(i));
    }
    for (var i = 0; i < 50; i++) {
      readings.Add(start.AddHours(-1).AddMilliseconds(i));
    }
    readings.Add(start.AddDays(-1));
    readings.Add(start.AddMilliseconds(200));

    var at = 0;
    var previous = RecordIdentity.Next();
    using (EngineClock.Override(() => readings[at])) {
      for (at = 0; at < readings.Count; at++) {
        var recorded = readings[at];
        var next = RecordIdentity.Next();
        Assert.True(next.CompareTo(previous) > 0, $"identifier {at} did not ascend after the clock read {recorded:O}");
        //Logical time is the identifier's timestamp, to the millisecond the Ulid keeps.
        Assert.True(next.Time >= recorded.AddMilliseconds(-1),
          $"logical time {next.Time:O} is before recorded time {recorded:O}");
        previous = next;
      }
    }
  }

  //A reader of the file as it is, for every identifier the database holds: every record's
  //identity and version identifier, catalogue descriptors included.
  private static List<Ulid> EveryStoredIdentifier(TempDatabaseFile file) {
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var firstPages = reader.Collections.Select(collection => collection.DataFirstPage).ToList();

    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var identifiers = new List<Ulid>();
    foreach (var first in firstPages) {
      var next = first;
      while (next != default) {
        var page = pageManager.LoadPage<DataPage>(next);
        foreach (var header in page.GetItems().Select(StoredRecordUtilities.ReadHeader)) {
          identifiers.Add(header.RecordId);
          identifiers.Add(header.VersionId);
        }
        next = page.NextPageIndex;
      }
    }
    return identifiers;
  }

  [Fact]
  public void AfterAResetWithTheClockAnHourBehindAndAReopenTheNextIdentifierIsAboveEverythingStored() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      db.CreateCollection("Person", [
        new ColumnDescriptor("Id", ValueTypeEnum.Int),
        new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
        new ColumnDescriptor("Age", ValueTypeEnum.Int),
        new ColumnDescriptor("Passport", ValueTypeEnum.Object),
        new ColumnDescriptor("Tags", ValueTypeEnum.Array)
      ]);
      db.CreateIndex("Person", "Age");
      var people = db.Entities<Person>("Person");
      for (var i = 0; i < 20; i++) {
        people.Insert(TestPeople.Numbered(i));
      }
      var first = people.GetAllRecords().First();
      people.Update(first.RecordId, TestPeople.Numbered(99));
      db.SetDisplayRule("Person", "{Name}");
    }
    var stored = EveryStoredIdentifier(file);
    var greatest = stored.Max();
    Assert.True(stored.Count > 40, "the database should hold many identifiers");

    RecordIdentity.ResetForTests();
    using (EngineClock.Override(() => DateTimeOffset.UtcNow.AddHours(-1))) {
      //Without the mark, a fresh process with this clock would mint below what is stored.
      Assert.True(Ulid.NewUlid(EngineClock.Now).CompareTo(greatest) < 0, "the stepped-back clock is not behind the stored identifiers");

      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var next = RecordIdentity.Next();
      Assert.True(next.CompareTo(greatest) > 0, $"{next} is not above the greatest stored identifier {greatest}");

      //And a write made now sorts after everything as well.
      var people = reopened.Entities<Person>("Person");
      var record = people.GetAllRecords().First();
      people.Update(record.RecordId, TestPeople.Numbered(123));
    }
    Assert.True(EveryStoredIdentifier(file).Max().CompareTo(greatest) > 0);
  }

  [Fact]
  public void TheMarkIsStoredByEveryCommitThatMovedIt() {
    using var file = new TempDatabaseFile();
    Ulid afterCreate;
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      afterCreate = db.Collection(SystemCollections.Collections).LastIdentifier;
      Assert.NotEqual(default, afterCreate);
      db.Entities<Person>().Insert(TestPeople.Ivan());
      var afterInsert = db.Collection(SystemCollections.Collections).LastIdentifier;
      Assert.True(afterInsert.CompareTo(afterCreate) > 0, "an insert mints and moves the mark");
      //Nothing minted after the mark was stamped: the mark is the last identifier issued.
      Assert.Equal(RecordIdentity.Last, afterInsert);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var mark = reopened.Collection(SystemCollections.Collections).LastIdentifier;
    Assert.True(mark.CompareTo(afterCreate) > 0);
    Assert.True(EveryStoredIdentifier(file).All(identifier => identifier.CompareTo(mark) <= 0),
      "every stored identifier is at or below the mark");
  }

  [Fact]
  public void ATransactionThatMintsNothingDirtiesNoPageForTheMark() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      var people = db.Entities<Person>();
      for (var i = 0; i < 5; i++) {
        people.Insert(TestPeople.Numbered(i));
      }
    }
    var catalogueBefore = CataloguePages(file);

    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      //An index write with nothing minted: the entry that is already there, put back as it is.
      var tree = db.PrimaryIndex("Person");
      var entry = tree.Scan().First();
      var pagesBefore = file.PageCount;
      db.InTransaction(() => tree.Upsert(entry.Key, entry.Address));
      Assert.Equal(pagesBefore, file.PageCount);
    }

    Assert.Equal(catalogueBefore, CataloguePages(file));
  }

  //The bytes of every page of the catalogue's own data chain.
  private static byte[] CataloguePages(TempDatabaseFile file) {
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var descriptor = reader.Collection(SystemCollections.Collections);
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    var pageSize = RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize;
    pageManager.SetPageSize(pageSize);
    var bytes = new List<byte>();
    var next = descriptor.DataFirstPage;
    while (next != default) {
      var page = pageManager.LoadPage<DataPage>(next);
      bytes.AddRange(page.Buffer.ToArray());
      next = page.NextPageIndex;
    }
    return bytes.ToArray();
  }
}
