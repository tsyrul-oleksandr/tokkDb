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
      Parent.Pages.UnionWith(Pages);
    }
    State = TransactionState.Committed;
    OnTransactionFinish();
  }

  public void Rollback() {
    RequireActive();
    Pages.Clear();
    State = TransactionState.RolledBack;
    Parent?.MarkRollbackOnly();
    OnTransactionFinish();
  }

  public void Track(BasePage page) {
    RequireActive();
    Pages.Add(page);
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
