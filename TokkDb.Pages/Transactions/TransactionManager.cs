using TokkDb.Pages;
using TokkDb.Pages.Transactions;
using TokkDb.Pages.Versions;

namespace TokkDb.Transactions;

public class TransactionManager {
  private readonly PageManager _pageManager;
  private ulong _lastTransactionId;

  public Transaction Current { get; set; }

  //Runs inside every outermost transaction that has pages to write, just before its pages are
  //committed, while it is still the current transaction. The connection wires the catalogue's
  //identifier mark here (HS-7): what the hook dirties joins the same commit.
  public Action BeforeOutermostCommit { get; set; }

  //HS-9: what an outermost transaction is stamped with as it begins. The connection wires its
  //attribution scope here; null means no scope, which stamps the empty attribution.
  public Func<VersionAttribution> AttributionSource { get; set; }

  //What brings the in-memory catalogues back in step with the file after an outermost
  //transaction is rolled back — record counts, page allocations and free space move in memory
  //as a transaction runs, and a rollback restores only the file. The connection wires its
  //catalogue reload here; a caller that rolls back deliberately and goes on working, as the
  //purge does for a record it cannot rebuild (RP-1), invokes it.
  public Action AfterOutermostRollback { get; set; }

  public TransactionManager(PageManager pageManager) {
    _pageManager = pageManager;
  }

  public Transaction CreateTransaction() {
    var transaction = new Transaction(++_lastTransactionId, _pageManager, this) {
      Parent = Current
    };
    if (transaction.IsOutermost) {
      transaction.Attribution = AttributionSource?.Invoke() ?? VersionAttribution.None;
    }
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
