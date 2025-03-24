namespace TokkDb.Pages.Managers;

public class EntityNotFoundException : Exception {
  public EntityNotFoundException(string message, Exception inner = null) : base(message, inner) { }
}
