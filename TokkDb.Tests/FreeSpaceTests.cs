using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//ST-1, ST-4, VR-13 and D-6.
public class FreeSpaceTests {
  private readonly ITestOutputHelper _output;

  public FreeSpaceTests(ITestOutputHelper output) {
    _output = output;
  }

  private static DataPage NewPage(uint index = 1) {
    return new DataPage {
      Buffer = new PageBuffer(new byte[TokkConstants.DefaultPageSize]),
      Index = index,
      Type = PageType.Data,
      PageSize = TokkConstants.DefaultPageSize
    };
  }

  //ST-1's acceptance criterion, and the done-when of this step.
  [Fact]
  public void ADeleteHeavyWorkloadDoesNotGrowTheFileMonotonically() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();

    for (var i = 0; i < 400; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }
    var pagesWhenFull = file.PageCount;

    //Churn: delete everything and insert as much again, many times over. Without space being
    //returned and reused the file would grow by the whole working set each round.
    for (var round = 0; round < 6; round++) {
      foreach (var record in entities.GetAllRecords().ToList()) {
        entities.Delete(record.RecordId);
      }
      Assert.Empty(entities.GetAll());
      for (var i = 0; i < 400; i++) {
        entities.Insert(TestPeople.Numbered(i));
      }
    }

    _output.WriteLine($"pages when first full: {pagesWhenFull}, after six rounds: {file.PageCount}");
    Assert.Equal(400, entities.GetAll().Count());
    //Six times the data passed through the file; the file did not grow six times over.
    Assert.True(file.PageCount <= pagesWhenFull + 2,
      $"the file grew from {pagesWhenFull} to {file.PageCount} pages under a delete-heavy workload");
  }

  //VR-13's acceptance criterion, and the second half of the done-when.
  [Fact]
  public void TheCatalogueRecordCountMatchesTheNumberOfLiveImages() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      var entities = db.Entities<Person>();
      for (var i = 0; i < 120; i++) {
        entities.Insert(TestPeople.Numbered(i));
      }
      var records = entities.GetAllRecords().ToList();
      foreach (var record in records.Where(record => record.Value.Id % 3 == 0)) {
        entities.Delete(record.RecordId);
      }
      foreach (var record in records.Where(record => record.Value.Id % 3 == 1)) {
        entities.Update(record.RecordId, TestPeople.Numbered(record.Value.Id));
      }
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var live = reopened.Entities<Person>().GetAll().Count();
    Assert.Equal(80, live);
    Assert.Equal((uint)live, reopened.Collection("Person").RecordCount);
  }

  [Fact]
  public void AnInsertNoLongerWalksTheWholePageChain() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();
    for (var i = 0; i < 400; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    var pages = file.PageCount;
    var readsBefore = db.PageReadCount;
    entities.Insert(TestPeople.Numbered(1000));
    var reads = db.PageReadCount - readsBefore;

    _output.WriteLine($"{pages} pages in the collection, {reads} page reads for one insert");
    //A walk down the chain would read every page of the collection. The free-space structure
    //says which page to go to, so the reads are a fixed handful — the page itself, the
    //catalogue page, the structure page and the before-images the commit takes of them —
    //however large the collection grows.
    Assert.True(reads < pages, $"one insert read {reads} pages of a {pages} page file");
    Assert.True(reads <= 12, $"one insert read {reads} pages, which does not look like a constant");
  }

  [Fact]
  public void TheFreeSpaceStructureIsRecordedInTheCatalogueAndSurvivesAReopen() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      for (var i = 0; i < 50; i++) {
        db.Entities<Person>().Insert(TestPeople.Numbered(i));
      }
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var person = reopened.Collection("Person");
    Assert.NotEqual(0u, person.FreeSpaceRoot);
    //And the catalogue's own structure, since it is a collection like any other.
    Assert.NotEqual(0u, reopened.Collection(SystemCollections.Collections).FreeSpaceRoot);

    //Inserting after the reopen uses the structure that was read back, not a fresh one.
    reopened.Entities<Person>().Insert(TestPeople.Numbered(99));
    Assert.Equal(51, reopened.Entities<Person>().GetAll().Count());
  }

  [Fact]
  public void BlockStateFollowsWhatThePageHolds() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();
    for (var i = 0; i < 200; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    var states = ReadBlockStates(file, "Person");
    Assert.Contains(BlockState.Occupied, states);
    //The structure's own pages are never handed out for records.
    Assert.Contains(BlockState.Reserved, ReadBlockStates(file, "Person"));

    foreach (var record in entities.GetAllRecords().ToList()) {
      entities.Delete(record.RecordId);
    }
    //Emptied pages report themselves free rather than staying nominally occupied.
    Assert.Contains(BlockState.Free, ReadBlockStates(file, "Person"));
  }

  [Fact]
  public void CompactionClosesGapsAndKeepsSlotIdentity() {
    var page = NewPage();
    var slots = new List<ushort>();
    for (ushort i = 0; i < 10; i++) {
      var slice = page.RegisterItem(100);
      slice.WriteInt(1000 + i, 0, out _);
      slots.Add(i);
    }

    //Free every other record, leaving the page full of holes.
    for (ushort i = 1; i < 10; i += 2) {
      page.FreeItem(i);
    }
    var reclaimableBefore = page.ReclaimableBytes;
    Assert.True(page.FreeListBytes > 0);

    page.Compact();

    //D-2 and ST-4: the records moved, their slot indexes did not.
    Assert.Equal(0, page.FreeListBytes);
    Assert.Equal(reclaimableBefore, page.ReclaimableBytes);
    for (ushort i = 0; i < 10; i += 2) {
      Assert.False(page.IsItemFree(i));
      Assert.Equal(1000 + i, page.GetItem(i).ReadInt(0, out _));
    }
    for (ushort i = 1; i < 10; i += 2) {
      Assert.True(page.IsItemFree(i));
    }
    //The freed bytes are one run now, so a record none of the holes could take fits.
    Assert.True(page.CanFit(400));
  }

  [Fact]
  public void CompactionMakesScatteredSpaceUsableForABiggerRecord() {
    var page = NewPage();
    while (page.CanFit(120)) {
      page.RegisterItem(120);
    }
    var filled = page.ItemsCount;
    for (ushort i = 0; i < filled; i += 2) {
      page.FreeItem(i);
    }

    //Half the page is free, but in 120 byte pieces that a 500 byte record cannot use.
    Assert.False(page.CanFit(500));
    Assert.True(page.ReclaimableBytes > 500);

    page.Compact();
    Assert.True(page.CanFit(500));
  }

  private static IReadOnlyList<BlockState> ReadBlockStates(TempDatabaseFile file, string collectionName) {
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();

    var states = new List<BlockState>();
    var next = reader.Collection(collectionName).FreeSpaceRoot;
    while (next != default) {
      var page = pageManager.LoadPage<FreeSpacePage>(next);
      states.AddRange(page.Entries.Select(entry => entry.State));
      next = page.NextPageIndex;
    }
    return states;
  }
}
