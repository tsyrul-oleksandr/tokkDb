using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;
using Xunit;

namespace TokkDb.Tests;

public class DiskRoundTripTests {
  private static PageBuffer PageWithIndex(uint index, byte fill) {
    var bytes = new byte[TokkConstants.DefaultPageSize];
    Array.Fill(bytes, fill);
    var buffer = new PageBuffer(bytes);
    buffer.WriteUInt(index, PageBuffer.IndexBufferPosition, out _);
    return buffer;
  }

  [Fact]
  public void PageRoundTripsThroughTheFile() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
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
    using var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(1, 0xCD));

    var onDisk = File.ReadAllBytes(file.Path);
    Assert.Equal(2 * TokkConstants.DefaultPageSize, onDisk.Length);
    Assert.Equal(0xCD, onDisk[TokkConstants.DefaultPageSize + 64]);
  }

  [Fact]
  public void FlushingAWrittenPageLeavesItReadable() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(0, 0xEE));
    disk.Flush();

    Assert.Equal(0xEE, File.ReadAllBytes(file.Path)[64]);
    Assert.Equal(0xEEu, disk.ReadPage(0).ReadByte(64));
  }

  [Fact]
  public void ABlankFileIsReportedBlankUntilAPageIsWritten() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    Assert.True(disk.IsBlank());
    disk.WritePage(PageWithIndex(0, 0x01));
    Assert.False(disk.IsBlank());
  }

  [Fact]
  public void TheFileHandleIsHeldForTheLifetimeOfTheManagerAndReleasedOnDispose() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(0, 0x11));

    Assert.ThrowsAny<IOException>(() => new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));

    disk.Dispose();
    using var exclusive = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Assert.Equal(TokkConstants.DefaultPageSize, exclusive.Length);
  }

  [Fact]
  public void ReadersMayShareTheOpenFile() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(0, 0x22));

    using var reader = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    Assert.Equal(TokkConstants.DefaultPageSize, reader.Length);
  }

  [Fact]
  public void ManyReadsAndWritesReuseTheSameHandle() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    disk.WritePage(PageWithIndex(0, 0x11));
    for (var i = 0; i < 200; i++) {
      _ = disk.IsBlank();
      _ = disk.ReadPage(0);
      disk.WritePage(PageWithIndex(0, 0x11));
    }

    Assert.Equal(1, file.PageCount);
    Assert.Equal(0x11u, disk.ReadPage(0).ReadByte(64));
  }
}
