using TokkDb.Core.Pages.Fields;

namespace TokkDb.Core.Pages;

public static class PageUtilities {
  public static long GetPosition(uint index) {
    return index * TokkConstants.PageSize;
  }
  
  public static long GetPosition(this PageBuffer buffer) {
    return GetPosition(buffer.Index);
  }
  
  public static void LoadFields(this IEnumerable<IPageField> fields, PageBuffer buffer, int startPosition) {
    var position = startPosition;
    foreach (var field in fields) {
      field.Read(buffer, position);
      position += field.Size;
    }
  }
  
  public static void SaveFields(this IEnumerable<IPageField> fields, PageBuffer buffer, int startPosition) {
    var position = startPosition;
    foreach (var field in fields) {
      field.Write(buffer, position);
      position += field.Size;
    }
  }
}
