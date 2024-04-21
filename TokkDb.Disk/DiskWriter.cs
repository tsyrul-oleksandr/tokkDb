using TokkDb.Core;
using TokkDb.Core.Pages;
using TokkDb.Disk.Cache;
using TokkDb.Disk.Streams;

namespace TokkDb.Disk;

public class DiskWriter {
  public IStreamFactory Factory { get; }
  public PageMemoryCache Cache { get; }

  public DiskWriter(IStreamFactory factory, PageMemoryCache cache) {
    Factory = factory;
    Cache = cache;
  }
  
  public virtual void WritePages(IEnumerable<PageBuffer> pages) {
    var stream = Factory.Get(false);
    foreach (var page in pages) {
      stream.Position = page.GetPosition();
      var buffer = page.ToArray();
      stream.Write(buffer, 0, TokkConstants.PageSize);
      Cache.Set(page.Index, page);
    }
    SaveStream(stream);
    
  }

  protected virtual void SaveStream(Stream stream) {
    if (stream is FileStream fileStream) {
      fileStream.Flush(true);
    }
    stream.Flush();
  }
}
