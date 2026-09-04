using TokkDb.Configuration;
using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

//Owns page 0: the page size in force, where the collections catalogue starts, and the page
//allocation counter.
public class RootPageManager {
  private readonly PageManager _pageManager;
  private readonly TransactionManager _transactionManager;
  private RootPage _rootPage;

  public RootPageManager(PageManager pageManager, TransactionManager transactionManager) {
    _pageManager = pageManager;
    _transactionManager = transactionManager;
  }

  public ushort PageSize => GetRootPage().PageSize;
  public ushort FormatVersion => GetRootPage().FormatVersion;
  public string MagicNumber => GetRootPage().MagicNumber;
  public DateTime CreatedAt => GetRootPage().CreatedAt;
  public uint LastAllocatedPageId => GetRootPage().LastAllocatedPageId;
  public uint CollectionsFirstPageId => GetRootPage().CollectionsFirstPageId;
  public uint CollectionsPrimaryIndexRoot => GetRootPage().CollectionsPrimaryIndexRoot;

  public void Initialize() {
    _rootPage = _pageManager.IsBlank() ? CreateNewRootPage() : OpenExistingRootPage();
  }

  public uint AllocatePageIndex() {
    var rootPage = GetRootPage();
    rootPage.LastAllocatedPageId++;
    _transactionManager.Track(rootPage);
    return rootPage.LastAllocatedPageId;
  }

  public void SetCollectionsFirstPageId(uint pageIndex) {
    var rootPage = GetRootPage();
    rootPage.CollectionsFirstPageId = pageIndex;
    _transactionManager.Track(rootPage);
  }

  public void SetCollectionsPrimaryIndexRoot(uint pageIndex) {
    var rootPage = GetRootPage();
    rootPage.CollectionsPrimaryIndexRoot = pageIndex;
    _transactionManager.Track(rootPage);
  }

  protected virtual RootPage CreateNewRootPage() {
    var rootPage = _pageManager.CreateNewMemoryPage<RootPage>(PageType.Root, TokkConstants.RootPageIndex);
    rootPage.MagicNumber = RootPage.ExpectedMagicNumber;
    rootPage.FormatVersion = RootPage.CurrentFormatVersion;
    rootPage.CreatedAt = DateTime.UtcNow;
    //Page 0 is the last page allocated so far; the catalogue and the data pages follow it.
    rootPage.LastAllocatedPageId = TokkConstants.RootPageIndex;
    _transactionManager.Track(rootPage);
    return rootPage;
  }

  protected virtual RootPage OpenExistingRootPage() {
    var prefix = _pageManager.ReadPrefix(RootPage.PrefixByteSize);
    //Throws unless the file names itself as a database of the format this build writes. No
    //byte past the prefix is read until it does.
    var header = RootPage.ReadPrefix(prefix);
    _pageManager.SetPageSize(header.PageSize);
    return _pageManager.LoadPage<RootPage>(TokkConstants.RootPageIndex);
  }

  protected virtual RootPage GetRootPage() {
    return _rootPage ?? throw new InvalidOperationException("The root page has not been loaded yet.");
  }
}
