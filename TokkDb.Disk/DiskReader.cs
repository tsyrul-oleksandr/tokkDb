using TokkDb.Core;
using TokkDb.Core.Pages;
using TokkDb.Disk.Cache;
using TokkDb.Disk.Streams;

namespace TokkDb.Disk;

public class DiskReader {
  public IStreamFactory Factory { get; }
  public PageMemoryCache Cache { get; }

  public DiskReader(IStreamFactory factory, PageMemoryCache cache) {
    Factory = factory;
    Cache = cache;
  }

  public PageBuffer ReadPage(uint index) {
    var stream = GetStream();
    var position = GetPosition(index);
    stream.Position = position;
    var bytes = new byte[TokkConstants.PageSize];
    _ = stream.Read(bytes, 0, bytes.Length);
    return new PageBuffer(bytes);
  }

  protected virtual long GetPosition(uint index) {
    return PageUtilities.GetPosition(index);
  }

  protected virtual Stream GetStream() {
    return Factory.Get(true);
  }
}
