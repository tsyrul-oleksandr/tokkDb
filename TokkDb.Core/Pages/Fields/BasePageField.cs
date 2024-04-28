namespace TokkDb.Core.Pages.Fields;

public abstract class BasePageField<T> : IPageField {
  public virtual int Size { get; }
  public virtual T Value { get; set; }

  public BasePageField() { }
  public BasePageField(int size) {
    Size = size;
  }

  public abstract void Read(PageBuffer buffer, int position);
  public abstract void Write(PageBuffer buffer, int position);
}
