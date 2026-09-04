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
  public Journal Journal { get; }
  public ushort PageSize { get; private set; }
  public long PageReadCount => Reader.PageReadCount;

  public DiskManager(string filePath, ushort pageSize = TokkConstants.DefaultPageSize) {
    PageSize = pageSize;
    //bufferSize 1 disables the user space buffer: pages reach the operating system as they
    //are written, and durability is what Flush is for.
    _stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
    Reader = new DiskReader(_stream, pageSize);
    Writer = new DiskWriter(_stream, pageSize);
    Journal = new Journal(filePath, pageSize);
  }

  //How many whole pages the file holds. Recorded in the journal so that a transaction which
  //grew the file can be undone by truncating it back.
  public uint PageCount => (uint)(_stream.Length / PageSize);

  //The page size of an existing file is stored in its root page, so it is known only after
  //the first bytes of the file have been read.
  public void SetPageSize(ushort pageSize) {
    PageSize = pageSize;
    Reader.PageSize = pageSize;
    Writer.PageSize = pageSize;
    Journal.PageSize = pageSize;
  }

  //Step one of the commit protocol: what the pages about to be written looked like before,
  //on the device, before the database file is touched at all.
  public void WriteJournal(ulong transactionId, IReadOnlyCollection<uint> pageIndexes) {
    var originalPageCount = PageCount;
    Journal.Begin(transactionId, originalPageCount, pageIndexes.Count);
    foreach (var pageIndex in pageIndexes) {
      var beforeImage = pageIndex < originalPageCount ? Reader.ReadPage(pageIndex).ToArray() : null;
      Journal.WriteBeforeImage(pageIndex, beforeImage);
    }
    Journal.Flush();
  }

  //The last step, once the database file itself is durable.
  public void CommitJournal(ulong transactionId) {
    Journal.MarkCommitted(transactionId);
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
    Journal.Dispose();
    _stream.Dispose();
  }
}
