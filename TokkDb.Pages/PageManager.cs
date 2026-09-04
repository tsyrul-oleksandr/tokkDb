using TokkDb.Buffer;
using TokkDb.Disk;

namespace TokkDb.Pages;

public class PageManager {
  private readonly DiskManager _diskManager;

  public PageManager(DiskManager diskManager) {
    _diskManager = diskManager;
  }

  public ushort PageSize => _diskManager.PageSize;
  public long PageReadCount => _diskManager.PageReadCount;

  public virtual bool IsBlank() {
    return _diskManager.IsBlank();
  }

  //Only the root page manager needs this: the bytes that say how big every other page is.
  public BufferSlice ReadPrefix(int length) {
    return _diskManager.ReadPrefix(length);
  }

  public void SetPageSize(ushort pageSize) {
    _diskManager.SetPageSize(pageSize);
  }

  public T CreateNewMemoryPage<T>(PageType type, uint index) where T : BasePage, new() {
    var buffer = CreateNewPageBuffer();
    var newPage = new T {
      Buffer = buffer,
      Index = index,
      Type = type,
      PageSize = _diskManager.PageSize
    };
    return newPage;
  }
  
  public T LoadPage<T>(uint index) where T : BasePage, new() {
    var buffer = _diskManager.ReadPage(index);
    var newPage = new T {
      Buffer = buffer,
      //The index that was asked for, so a damaged page is named by where it was read from
      //rather than by whatever its own bytes claim. Load overwrites it once the page checks out.
      Index = index,
      PageSize = _diskManager.PageSize
    };
    newPage.Load();
    return newPage;
  }
  
  //The commit protocol of TX-2, in the order that makes it recoverable: the journal first
  //and on the device, then the database file, then the commit record.
  public virtual void CommitPages(ulong transactionId, BasePage[] pages) {
    if (pages.Length == 0) {
      return;
    }
    foreach (var page in pages) {
      page.Save();
    }
    WriteJournal(transactionId, pages);
    WritePages(pages);
    MarkJournalCommitted(transactionId);
  }

  protected virtual void WriteJournal(ulong transactionId, BasePage[] pages) {
    _diskManager.WriteJournal(transactionId, pages.Select(page => page.Index).ToArray());
  }

  protected virtual void WritePages(BasePage[] pages) {
    foreach (var page in pages) {
      _diskManager.WritePage(page.Buffer);
    }
    _diskManager.Flush();
  }

  protected virtual void MarkJournalCommitted(ulong transactionId) {
    _diskManager.CommitJournal(transactionId);
  }

  public void SavePages<T>(params T[] pages) where T : BasePage {
    foreach (var page in pages) {
      page.Save();
      _diskManager.WritePage(page.Buffer);
    }
  }

  //Called by a committing transaction, never by an individual page write.
  public void Flush() {
    _diskManager.Flush();
  }

  private PageBuffer CreateNewPageBuffer() {
    var buffer = new byte[_diskManager.PageSize];
    return new PageBuffer(buffer);
  }
}
