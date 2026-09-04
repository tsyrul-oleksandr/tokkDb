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
}
