using TokkDb.Buffer;
using TokkDb.Configuration;

namespace TokkDb.Disk;

public class DiskWriter {
  private readonly string _filePath;

  public DiskWriter(string filePath) {
    _filePath = filePath;
  }
  
  public void WritePage(PageBuffer pageBuffer) {
    using var stream = GetStream();
    stream.Position = (long)pageBuffer.Index * TokkConstants.PageSize;
    var buffer = pageBuffer.ToArray();
    stream.Write(buffer, 0, TokkConstants.PageSize);
  }

  private Stream GetStream() {
    return new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write);
  }
}
