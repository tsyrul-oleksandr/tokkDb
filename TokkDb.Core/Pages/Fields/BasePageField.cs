namespace TokkDb.Core.Pages.Fields;

public abstract class BasePageField<T> : IPageField {
  public virtual T Value { get; set; }

  public abstract int Read(PageBuffer buffer, int position);
  public abstract int Write(PageBuffer buffer, int position);
}
