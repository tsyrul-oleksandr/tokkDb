using TokkDb.Buffer;
using TokkDb.Configuration;

namespace TokkDb.Disk;

public class DiskReader {
  private readonly string _filePath;

  public DiskReader(string filePath) {
    _filePath = filePath;
  }
  
  public bool IsBlank() {
    var stream = GetStream(TokkConstants.PageSize);
    return stream.Length < TokkConstants.PageSize;
  }
  
  public PageBuffer ReadPage(uint index) {
    var stream = GetStream(TokkConstants.PageSize);
    var position = index * TokkConstants.PageSize;
    stream.Position = position;
    var bytes = new byte[TokkConstants.PageSize];
    _ = stream.Read(bytes, 0, bytes.Length);
    return new PageBuffer(bytes);
  }
  
  private Stream GetStream(int bufferSize) {
    return new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read, bufferSize);
  }
}
