namespace TokkDb.Core.Pages.Headers;

public struct HeaderField {
  public int Index { get; }
  public int Size { get; }
  public int NextPosition => Index + Size;

  public HeaderField(int index, int size) {
    Index = index;
    Size = size;
  }
}
