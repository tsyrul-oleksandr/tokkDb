namespace TokkDb.Pages.Transactions;

public class TransactionStateException : Exception {
  public TransactionStateException(string message, Exception inner = null) : base(message, inner) { }
}
