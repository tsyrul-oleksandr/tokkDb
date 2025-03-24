using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;

namespace TokkDb.Pages;

public class PageManager {
  private readonly DiskManager _diskManager;

  public PageManager(DiskManager diskManager) {
    _diskManager = diskManager;
  }
  
  public virtual bool IsBlank() {
    return _diskManager.IsBlank();
  }
  
  public T CreateNewMemoryPage<T>(PageType type, uint index) where T : BasePage, new() {
    var buffer = CreateNewPageBuffer();
    var newPage = new T {
      Buffer = buffer,
      Index = index,
      Type = type
    };
    return newPage;
  }
  
  public T LoadPage<T>(uint index) where T : BasePage, new() {
    var buffer = _diskManager.ReadPage(index);
    var newPage = new T {
      Buffer = buffer
    };
    newPage.Load();
    return newPage;
  }
  
  public void SavePages<T>(params T[] pages) where T : BasePage {
    foreach (var page in pages) {
      page.Save();
      _diskManager.WritePage(page.Buffer);
    }
  }

  private PageBuffer CreateNewPageBuffer() {
    var buffer = new byte[TokkConstants.PageSize];
    return new PageBuffer(buffer);
  }
}
