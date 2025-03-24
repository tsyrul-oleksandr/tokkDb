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
  

  public void Track(BasePage page) {
    if (Current == null) {
      throw new TransactionNotFoundException();
    }
    Current?.Track(page);
  }
}
