using TokkDb.Buffer;
using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

public class DataPageManager {
  private readonly PageManager _pageManager;
  private readonly MetadataPageManager _metadataPageManager;
  private readonly TransactionManager _transactionManager;

  public DataPageManager(PageManager pageManager, MetadataPageManager metadataPageManager, 
      TransactionManager transactionManager) {
    _pageManager = pageManager;
    _metadataPageManager = metadataPageManager;
    _transactionManager = transactionManager;
  }

  public BufferSlice Register(string entityName, ushort bytesLength) {
    var page = GetAvailablePage(entityName, bytesLength);
    _transactionManager.Track(page);
    return page.RegisterItem(bytesLength);
  }

  private DataPage GetAvailablePage(string entityName, ushort bytesLength) {
    foreach (var page in GetPages(entityName)) {
      if (page.FreeBytes > bytesLength) {
        return page;
      }
    }
    return CreateNewPage(entityName);
  }

  public IEnumerable<BufferSlice> GetAll(string entityName) {
    foreach (var page in GetPages(entityName)) {
      foreach (var item in page.GetItems()) {
        yield return item;
      }
    }
  }
  
  protected virtual IEnumerable<DataPage> GetPages(string entityName) {
    var nextPageIndex = _metadataPageManager.GetFirstPageIndex(entityName);
    while (nextPageIndex != default) {
      var page = _pageManager.LoadPage<DataPage>(nextPageIndex);
      yield return page;
      nextPageIndex = page.NextPageIndex;
    }
  }
  
  protected virtual DataPage CreateNewPage(string entityName) {
    var newPageIndex = _metadataPageManager.GetNewPageIndex();
    var newPage = _pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, newPageIndex);
    var lastPageId = _metadataPageManager.GetLastPageIndex(entityName);
    if (lastPageId != default) {
      var previousLastPage = _pageManager.LoadPage<DataPage>(lastPageId);
      previousLastPage.NextPageIndex = newPageIndex;
    }
    _metadataPageManager.SetLastPageIndex(entityName, newPageIndex);
    _transactionManager.Track(newPage);
    return newPage;
  }
}
