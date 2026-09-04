using TokkDb.Pages;
using TokkDb.Pages.Transactions;

namespace TokkDb.Transactions;

public class TransactionManager {
  private readonly PageManager _pageManager;

  public Transaction Current { get; set; }
  
  public TransactionManager(PageManager pageManager) {
    _pageManager = pageManager;
  }

  public Transaction CreateTransaction() {
    var transaction = new Transaction(_pageManager, this);
    if (Current != null) {
      transaction.Parent = Current;
    }
    Current = transaction;
    return transaction;
  }
  

  //The identity map for the open transaction: a page changed once must not be read back from
  //disk and changed again through a second object.
  public T FindTrackedPage<T>(uint index) where T : BasePage {
    for (var transaction = Current; transaction != null; transaction = transaction.Parent) {
      var page = transaction.Pages.OfType<T>().FirstOrDefault(item => item.Index == index);
      if (page != null) {
        return page;
      }
    }
    return null;
  }

  public void Track(BasePage page) {
    if (Current == null) {
      throw new TransactionNotFoundException();
    }
    Current?.Track(page);
  }
}
