namespace TokkDb.Core.Pages;

public class BaseFieldAddress {
  public int Index { get; }
  public int Size { get; }
  public int NextPosition => Index + Size;

  public BaseFieldAddress() { }
  
  public BaseFieldAddress(int index, int size) {
    Index = index;
    Size = size;
  }
}
