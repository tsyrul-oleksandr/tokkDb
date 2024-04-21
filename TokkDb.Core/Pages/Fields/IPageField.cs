namespace TokkDb.Core.Pages.Fields;

public interface IPageField {
  int Size { get; }
  void Read(PageBuffer buffer, int position);
  void Write(PageBuffer buffer, int position);
}
