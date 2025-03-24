using TokkDb.Transactions;

namespace TokkDb.Pages.Transactions;

public class Transaction {
  private readonly PageManager _pageManager;
  private readonly TransactionManager _transactionManager;
  public HashSet<BasePage> Pages { get; } = [];

  public Transaction(PageManager pageManager, TransactionManager transactionManager) {
    _pageManager = pageManager;
    _transactionManager = transactionManager;
  }

  internal Transaction Parent { get; set; }

  public void Commit() {
    _pageManager.SavePages(Pages.ToArray());
    OnTransactionFinish();
  }

  public void Rollback() {
    Pages.Clear();
    OnTransactionFinish();
  }
  
  //Add check before
  private void OnTransactionFinish() {
    if (this == _transactionManager.Current) {
      _transactionManager.Current = null;
      _transactionManager.Current = Parent;
      return;
    }
    throw new Exception("Transaction sequence is corrupted.");
  }

  public void Track(BasePage page) {
    Pages.Add(page);
  }
}
