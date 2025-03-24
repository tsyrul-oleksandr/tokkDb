namespace TokkDb.Transactions;

public class TransactionNotFoundException : Exception {
  public TransactionNotFoundException(string message = "transaction not created", Exception inner = null) 
    : base(message, inner) { }
}
