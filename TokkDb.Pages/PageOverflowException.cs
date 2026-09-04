namespace TokkDb.Pages;

public class PageOverflowException : Exception {
  public PageOverflowException(string message, Exception inner = null) : base(message, inner) { }
}
