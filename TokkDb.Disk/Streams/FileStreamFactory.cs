using TokkDb.Core;

namespace TokkDb.Disk.Streams;

public class FileStreamFactory : IStreamFactory {
  protected string FilePath { get; }

  public FileStreamFactory(string filePath){
    FilePath = filePath;
  }
  
  public Stream Get(bool readOnly) {
    return CreateStream(readOnly);
  }
  
  protected virtual Stream CreateStream(bool readOnly) {
    var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
    var share = readOnly ? FileShare.Read : FileShare.ReadWrite;
    return new FileStream(FilePath, FileMode.OpenOrCreate, access, share, TokkConstants.PageSize);
  }
}
