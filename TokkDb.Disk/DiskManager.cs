using TokkDb.Buffer;
using TokkDb.Configuration;

namespace TokkDb.Disk;

//Owns the one file handle the connection uses. It is opened read/write and shared for
//reading only, so a second writer cannot attach to the same file, and it stays open until
//the connection is disposed instead of being reopened for every page.
public class DiskManager : IDisposable {
  private readonly FileStream _stream;
  private bool _disposed;

  public DiskReader Reader { get; }
  public DiskWriter Writer { get; }
  public ushort PageSize { get; private set; }

  public DiskManager(string filePath, ushort pageSize = TokkConstants.DefaultPageSize) {
    PageSize = pageSize;
    //bufferSize 1 disables the user space buffer: pages reach the operating system as they
    //are written, and durability is what Flush is for.
    _stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
    Reader = new DiskReader(_stream, pageSize);
    Writer = new DiskWriter(_stream, pageSize);
  }

  //The page size of an existing file is stored in its root page, so it is known only after
  //the first bytes of the file have been read.
  public void SetPageSize(ushort pageSize) {
    PageSize = pageSize;
    Reader.PageSize = pageSize;
    Writer.PageSize = pageSize;
  }

  public bool IsBlank() {
    return Reader.IsBlank();
  }

  //The first bytes of the file, read without knowing the page size yet.
  public BufferSlice ReadPrefix(int length) {
    return Reader.ReadPrefix(length);
  }

  public PageBuffer ReadPage(uint index) {
    return Reader.ReadPage(index);
  }

  public void WritePage(PageBuffer page) {
    Writer.WritePage(page);
  }

  //Durability point. Only a committing transaction may call it.
  public void Flush() {
    Writer.Flush();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _stream.Dispose();
  }
}
