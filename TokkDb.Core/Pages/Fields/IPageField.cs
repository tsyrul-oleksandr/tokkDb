namespace TokkDb.Core.Pages.Fields;

public interface IPageField {
  int Read(PageBuffer buffer, int position);
  int Write(PageBuffer buffer, int position);
}
