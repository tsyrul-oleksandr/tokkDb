using TokkDb.Buffer;

namespace TokkDb.Disk;

public class DiskReader {
  private readonly FileStream _stream;

  public ushort PageSize { get; set; }

  //How many pages have been read off the device. Diagnostics: it is what shows whether a
  //lookup was served from memory or cost a physical read.
  public long PageReadCount { get; private set; }

  public DiskReader(FileStream stream, ushort pageSize) {
    _stream = stream;
    PageSize = pageSize;
  }

  public bool IsBlank() {
    return _stream.Length == 0;
  }

  //Shorter files are zero padded rather than rejected here: whether the bytes describe a
  //database at all is for the root page to decide.
  public BufferSlice ReadPrefix(int length) {
    var bytes = new byte[length];
    _stream.Position = 0;
    _stream.ReadExactly(bytes, 0, (int)Math.Min(length, _stream.Length));
    return new BufferSlice(bytes);
  }

  public PageBuffer ReadPage(uint index) {
    PageReadCount++;
    var bytes = new byte[PageSize];
    _stream.Position = (long)index * PageSize;
    _stream.ReadExactly(bytes, 0, bytes.Length);
    return new PageBuffer(bytes);
  }
}
