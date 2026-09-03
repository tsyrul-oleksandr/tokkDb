using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;
using Xunit;

namespace TokkDb.Tests;

public class DiskRoundTripTests {
  private static PageBuffer PageWithIndex(uint index, byte fill) {
    var bytes = new byte[TokkConstants.PageSize];
    Array.Fill(bytes, fill);
    var buffer = new PageBuffer(bytes);
    buffer.WriteUInt(index, PageBuffer.IndexBufferPosition, out _);
    return buffer;
  }

  [Fact]
  public void PageRoundTripsThroughTheFile() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(0, 0xAA));
    disk.WritePage(PageWithIndex(3, 0xBB));

    Assert.Equal(4, file.PageCount);
    Assert.Equal(0xAAu, disk.ReadPage(0).ReadByte(64));
    Assert.Equal(0xBBu, disk.ReadPage(3).ReadByte(64));
    Assert.Equal(3u, disk.ReadPage(3).Index);
  }

  [Fact]
  public void WrittenPagesAreVisibleOnDiskImmediately() {
    using var file = new TempDatabaseFile();
    new DiskManager(file.Path).WritePage(PageWithIndex(1, 0xCD));

    var onDisk = File.ReadAllBytes(file.Path);
    Assert.Equal(2 * TokkConstants.PageSize, onDisk.Length);
    Assert.Equal(0xCD, onDisk[TokkConstants.PageSize + 64]);
  }

  [Fact]
  public void ABlankFileIsReportedBlankUntilAPageIsWritten() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    Assert.True(disk.IsBlank());
    disk.WritePage(PageWithIndex(0, 0x01));
    Assert.False(disk.IsBlank());
  }

  [Fact]
  public void ReadsAndWritesLeaveNoOpenHandles() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(0, 0x11));
    for (var i = 0; i < 200; i++) {
      _ = disk.IsBlank();
      _ = disk.ReadPage(0);
      disk.WritePage(PageWithIndex(0, 0x11));
    }

    using var exclusive = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Assert.Equal(TokkConstants.PageSize, exclusive.Length);
  }
}
