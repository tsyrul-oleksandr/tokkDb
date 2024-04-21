namespace TokkDb.Core.Pages.Fields;

public abstract class BasePageField<T> : IPageField {
  public int Size { get; }
  public T Value { get; set; }
  
  public BasePageField(int size) {
    Size = size;
  }

  public abstract void Read(PageBuffer buffer, int position);
  public abstract void Write(PageBuffer buffer, int position);
}
