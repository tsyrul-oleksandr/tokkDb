using TokkDb.Pages;
using TokkDb.Pages.Transactions;

namespace TokkDb.Transactions;

public class TransactionManager {
  private readonly PageManager _pageManager;
  private ulong _lastTransactionId;

  public Transaction Current { get; set; }
  
  public TransactionManager(PageManager pageManager) {
    _pageManager = pageManager;
  }

  public Transaction CreateTransaction() {
    var transaction = new Transaction(++_lastTransactionId, _pageManager, this) {
      Parent = Current
    };
    Current = transaction;
    return transaction;
  }

  //DC-8: a catalogue change and the data change it goes with belong to one transaction, so
  //every mutation has to find one already open.
  public Transaction RequireTransaction() {
    return Current ?? throw new TransactionNotFoundException();
  }
  

  //The identity map for the open transaction: a page changed once must not be read back from
  //disk and changed again through a second object.
  public T FindTrackedPage<T>(uint index) where T : BasePage {
    for (var transaction = Current; transaction != null; transaction = transaction.Parent) {
      if (transaction.FindPage(index) is T page) {
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
