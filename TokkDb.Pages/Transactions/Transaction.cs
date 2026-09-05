using TokkDb.Transactions;

namespace TokkDb.Pages.Transactions;

//A unit of work over a set of dirty pages. Only the outermost transaction reaches the
//device: an inner one hands its pages to the transaction that contains it, so that a nest
//of them either lands together or not at all.
public class Transaction {
  private readonly PageManager _pageManager;
  private readonly TransactionManager _transactionManager;

  public ulong Id { get; }
  public TransactionState State { get; private set; } = TransactionState.Active;
  public HashSet<BasePage> Pages { get; } = [];

  //The same pages by index. The set is what commits; this is what makes finding one of them
  //a lookup. A transaction that dirties a few thousand pages — one bulk index build does —
  //would otherwise spend its time scanning its own page set on every read.
  private readonly Dictionary<uint, BasePage> _pagesByIndex = [];

  //Set when an inner transaction rolls back. The work it undid was part of this one, so the
  //whole nest is doomed and the outermost commit must refuse rather than write half of it.
  public bool IsRollbackOnly { get; private set; }

  public Transaction(ulong id, PageManager pageManager, TransactionManager transactionManager) {
    Id = id;
    _pageManager = pageManager;
    _transactionManager = transactionManager;
  }

  internal Transaction Parent { get; set; }

  public bool IsOutermost => Parent is null;

  public void Commit() {
    RequireActive();
    if (IsRollbackOnly) {
      throw new TransactionStateException(
        $"Transaction {Id} cannot commit: a transaction nested inside it was rolled back.");
    }
    if (IsOutermost) {
      _pageManager.CommitPages(Id, Pages.ToArray());
    } else {
      //Nothing durable happens here. The pages become the containing transaction's problem.
      Parent.Absorb(this);
    }
    State = TransactionState.Committed;
    OnTransactionFinish();
  }

  public void Rollback() {
    RequireActive();
    Pages.Clear();
    _pagesByIndex.Clear();
    State = TransactionState.RolledBack;
    try {
      if (IsOutermost) {
        //Dropping the page set is not enough: a commit that failed between writing the pages
        //and recording the commit has already changed the file, and only the journal undoes that.
        _pageManager.RollbackPages(Id);
      }
    } finally {
      //The nest unwinds even when the undo itself fails, or the next transaction would open
      //inside a transaction that is already finished.
      Parent?.MarkRollbackOnly();
      OnTransactionFinish();
    }
  }

  public void Track(BasePage page) {
    RequireActive();
    //One object per page index, and the newer one wins. A page freed and handed out again
    //inside the same transaction — an index page a merge retired and a later split took
    //back — arrives here as a second object for an index the set already holds, and
    //committing both would write them in whichever order the set happened to keep.
    if (_pagesByIndex.TryGetValue(page.Index, out var superseded) && !ReferenceEquals(superseded, page)) {
      Pages.Remove(superseded);
    }
    Pages.Add(page);
    _pagesByIndex[page.Index] = page;
  }

  //The page this transaction already holds for that index, whatever its kind, or null. The
  //identity map: a page changed once must not be read back from disk and changed again
  //through a second object.
  internal BasePage FindPage(uint index) {
    return _pagesByIndex.GetValueOrDefault(index);
  }

  internal void Absorb(Transaction inner) {
    foreach (var page in inner.Pages) {
      Track(page);
    }
  }

  internal void MarkRollbackOnly() {
    IsRollbackOnly = true;
  }

  private void RequireActive() {
    if (State != TransactionState.Active) {
      throw new TransactionStateException($"Transaction {Id} is already {State}.");
    }
  }

  private void OnTransactionFinish() {
    if (this != _transactionManager.Current) {
      throw new TransactionStateException(
        $"Transaction {Id} did not finish in order: transactions must end innermost first.");
    }
    _transactionManager.Current = Parent;
  }
}
