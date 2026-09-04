using TokkDb.Buffer;

namespace TokkDb.Disk;

public class DiskWriter {
  private readonly FileStream _stream;

  public ushort PageSize { get; set; }

  public DiskWriter(FileStream stream, ushort pageSize) {
    _stream = stream;
    PageSize = pageSize;
  }

  public void WritePage(PageBuffer pageBuffer) {
    var buffer = pageBuffer.ToArray();
    _stream.Position = (long)pageBuffer.Index * PageSize;
    _stream.Write(buffer, 0, PageSize);
  }

  //Written pages are visible to readers immediately, but only this makes them survive a
  //crash, so it belongs to the commit path and nowhere else.
  public void Flush() {
    _stream.Flush(flushToDisk: true);
  }
}
