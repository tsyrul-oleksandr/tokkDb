using TokkDb.Core;
using TokkDb.Core.Pages;
using TokkDb.Disk.Cache;
using TokkDb.Disk.Streams;

namespace TokkDb.Disk;

public class DiskWriter {
  private Stream _stream = Stream.Null;
  public IStreamFactory Factory { get; }
  public PageMemoryCache Cache { get; }
  public Stream Stream => (_stream != Stream.Null) ? _stream : _stream = Factory.Get(false);

  public DiskWriter(IStreamFactory factory, PageMemoryCache cache) {
    Factory = factory;
    Cache = cache;
  }
  
  public virtual void WritePages(IEnumerable<PageBuffer> pages) {
    var stream = Stream;
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
