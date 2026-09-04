namespace TokkDb.Documents.Path.Expressions;

public class ExpressionException : Exception {
  public ExpressionException(string message, Exception inner = null) : base(message, inner) { }
}
