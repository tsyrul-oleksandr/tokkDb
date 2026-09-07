using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Tests;

public class ItemsPageTests {
  private const ushort SlotSize = 4;
  //Header at the front, control area at the back; items and their slots share what is left.
  private const ushort UsableBytes =
    TokkConstants.DefaultPageSize - BasePage.StartContentBufferPosition - BasePage.ControlAreaByteSize;

  private static DataPage NewPage(uint index = 1) {
    return new DataPage {
      Buffer = new PageBuffer(new byte[TokkConstants.DefaultPageSize]),
      Index = index,
      Type = PageType.Data,
      PageSize = TokkConstants.DefaultPageSize
    };
  }

  [Fact]
  public void FreshPageReportsTheWholeContentAreaAsFree() {
    var page = NewPage();
    Assert.Equal(UsableBytes, page.FreeBytes);
    Assert.Equal(0, page.ItemsCount);
  }

  [Fact]
  public void RegisterItemChargesForTheSlotAsWellAsTheItem() {
    var page = NewPage();
    page.RegisterItem(100);
    Assert.Equal(UsableBytes - 100 - SlotSize, page.FreeBytes);
    Assert.Equal(1, page.ItemsCount);
  }

  [Fact]
  public void CanFitRequiresRoomForTheSlotToo() {
    var page = NewPage();
    var exactlyTooBig = (ushort)(page.FreeBytes - SlotSize + 1);
    Assert.False(page.CanFit(exactlyTooBig));
    Assert.True(page.CanFit((ushort)(exactlyTooBig - 1)));
  }

  [Fact]
  public void RegisterItemThrowsRatherThanOverwritingTheSlotDirectory() {
    var page = NewPage();
    var tooBig = (ushort)(page.FreeBytes - SlotSize + 1);
    Assert.Throws<PageOverflowException>(() => page.RegisterItem(tooBig));
  }

  [Fact]
  public void ContentNeverGrowsIntoTheSlotDirectory() {
    var page = NewPage();
    const ushort itemSize = 135;
    while (page.CanFit(itemSize)) {
      page.RegisterItem(itemSize);
    }
    var slotDirectoryBottom =
      TokkConstants.DefaultPageSize - BasePage.ControlAreaByteSize - page.ItemsCount * SlotSize;
    Assert.True(page.NextFreePosition <= slotDirectoryBottom,
      $"content reached {page.NextFreePosition}, slot directory starts at {slotDirectoryBottom}");
  }

  [Fact]
  public void TheSlotDirectoryNeverReachesIntoTheControlArea() {
    var page = NewPage();
    const ushort itemSize = 8;
    while (page.CanFit(itemSize)) {
      page.RegisterItem(itemSize);
    }

    var lastSlotEnd = TokkConstants.DefaultPageSize - page.ItemsCount * SlotSize;
    Assert.True(lastSlotEnd <= TokkConstants.DefaultPageSize - BasePage.ControlAreaByteSize,
      $"the slot directory reached {lastSlotEnd}, the control area starts at " +
      $"{TokkConstants.DefaultPageSize - BasePage.ControlAreaByteSize}");
  }

  [Fact]
  public void ItemsStartAfterTheHeader() {
    var page = NewPage();
    Assert.Equal(BasePage.StartContentBufferPosition, page.NextFreePosition);
    page.RegisterItem(16);
    Assert.Equal(BasePage.StartContentBufferPosition + 16, page.NextFreePosition);
  }

  [Fact]
  public void EveryItemInAFullPageKeepsItsOwnContent() {
    var page = NewPage();
    const ushort itemSize = 135;
    var written = new List<int>();
    while (page.CanFit(itemSize)) {
      var slice = page.RegisterItem(itemSize);
      var marker = 1000 + written.Count;
      slice.WriteInt(marker, 0, out _);
      written.Add(marker);
    }

    Assert.NotEmpty(written);
    var read = page.GetItems().Select(item => item.ReadInt(0, out _)).ToList();
    Assert.Equal(written, read);
  }

  [Fact]
  public void APageHoldsMoreThanByteMaxValueItems() {
    var page = NewPage();
    const ushort itemSize = 8;
    var written = new List<int>();
    while (page.CanFit(itemSize)) {
      var slice = page.RegisterItem(itemSize);
      slice.WriteInt(written.Count, 0, out _);
      written.Add(written.Count);
    }

    Assert.True(page.ItemsCount > byte.MaxValue, $"page held only {page.ItemsCount} items");
    Assert.Equal(written, page.GetItems().Select(item => item.ReadInt(0, out _)).ToList());
  }

  [Fact]
  public void ItemSlicesDoNotOverlap() {
    var page = NewPage();
    var sizes = new ushort[] { 16, 32, 8, 64 };
    foreach (var size in sizes) {
      var slice = page.RegisterItem(size);
      slice.WriteBytes(Enumerable.Repeat((byte)size, size).ToArray(), 0, out _);
    }

    var items = page.GetItems().ToList();
    Assert.Equal(sizes.Length, items.Count);
    for (var i = 0; i < sizes.Length; i++) {
      Assert.All(items[i].ReadBytes(sizes[i], 0, out _), b => Assert.Equal(sizes[i], b));
    }
  }

  //D-2 depends on this: an index entry is (pageId, slotId), so a page that reports a slot the
  //item did not go into hands out an address no record was ever written to. The record is
  //still on the page, so a scan finds it and only the index is wrong — which is the kind of
  //fault that shows up long after the write that caused it.
  //
  //There are three placements and each has to report itself: an appended slot, a freed slot
  //reused, and a slot that compaction emptied.
  [Fact]
  public void RegisterItemReportsTheSlotItActuallyUsed() {
    var page = NewPage();

    //Appended.
    page.RegisterItem(100, out var appended);
    page.RegisterItem(100, out var second);
    page.RegisterItem(40, out var third);
    Assert.Equal(0, appended);
    Assert.Equal(1, second);
    Assert.Equal(2, third);

    //A freed slot, reused for something that fits it.
    page.FreeItem(1);
    var reusedBuffer = page.RegisterItem(80, out var reused);
    Assert.Equal(1, reused);
    reusedBuffer.WriteInt(4242, 0, out _);
    Assert.Equal(4242, page.GetItem(reused).ReadInt(0, out _));

    //A slot compaction emptied: it keeps its index and loses its space, so the next item
    //takes the index and space out of the contiguous run.
    page.FreeItem(0);
    page.Compact();
    page.RegisterItem(60, out var emptied);
    Assert.Equal(0, emptied);
    Assert.False(page.IsItemFree(emptied));
  }

  //The same thing where it matters: a record written into a slot compaction emptied has to be
  //findable through the index, which is only true if the address the write reported is the
  //slot it went into.
  [Fact]
  public void ARecordWrittenIntoAnEmptiedSlotIsFoundThroughTheIndex() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();

    var ids = new List<Ulid>();
    db.InTransaction(() => {
      for (var i = 0; i < 120; i++) {
        ids.Add(entities.Insert(TestPeople.Numbered(i)));
      }
    });
    //Frees slots and lets compaction empty them, so the next writes take that third path.
    foreach (var id in ids.Take(60)) {
      entities.Delete(id);
    }
    var written = new List<Ulid>();
    db.InTransaction(() => {
      for (var i = 200; i < 260; i++) {
        written.Add(entities.Insert(TestPeople.Numbered(i)));
      }
    });

    Assert.All(written, id => Assert.NotNull(entities.GetById(id)));
    Assert.Equal(120, entities.GetAll().Count());
  }

  //ST-4. Compaction slides live records down over the gaps, and the order it must do that in
  //is the order they lie in the page — not the order of their slots.
  //
  //Those two orders come apart as soon as a slot is handed out again: an emptied slot keeps
  //its low index and is given space at the end of the run, so slot 0 can hold bytes that lie
  //after slot 5's. Walking the directory in order then copies one record over another that
  //has not been moved yet, and the page reads back as garbage from that point on.
  [Fact]
  public void CompactionMovesRecordsInPageOrderRatherThanSlotOrder() {
    var page = NewPage();
    for (var i = 0; i < 4; i++) {
      page.RegisterItem(100, out var slot).WriteInt(1000 + i, 0, out _);
      Assert.Equal(i, slot);
    }

    //Free the first two and compact: their slots are emptied and keep their indexes.
    page.FreeItem(0);
    page.FreeItem(1);
    page.Compact();

    //Slot 0 is handed out again and given space at the end of the run, so its bytes now lie
    //after slot 2's and slot 3's.
    page.RegisterItem(100, out var reused).WriteInt(5555, 0, out _);
    Assert.Equal(0, reused);

    //Free something in the middle so there is a gap worth closing, then compact again.
    page.FreeItem(2);
    page.Compact();

    Assert.Equal(5555, page.GetItem(0).ReadInt(0, out _));
    Assert.Equal(1003, page.GetItem(3).ReadInt(0, out _));
    Assert.True(page.IsItemFree(1));
    Assert.True(page.IsItemFree(2));
  }

  //The same thing at the level it matters: records written into reused slots survive the
  //compaction that the next write triggers.
  [Fact]
  public void RecordsSurviveCompactionAfterTheirSlotsHaveBeenHandedOutAgain() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();

    //Enough churn to fill pages, free slots, compact them and hand the emptied slots out.
    var ids = new List<Ulid>();
    db.InTransaction(() => {
      for (var i = 0; i < 300; i++) {
        ids.Add(entities.Insert(TestPeople.Numbered(i)));
      }
    });
    for (var round = 0; round < 3; round++) {
      foreach (var id in ids.Where((_, i) => i % 3 == round).ToList()) {
        entities.Update(id, TestPeople.Numbered(900 + round));
      }
    }

    var all = entities.GetAll().ToList();
    Assert.Equal(300, all.Count);
    Assert.All(ids, id => Assert.NotNull(entities.GetById(id)));
    Assert.All(all, person => Assert.StartsWith("Person-", person.Name));
  }
}
