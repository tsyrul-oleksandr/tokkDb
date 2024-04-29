using TokkDb.Core.Pages.Fields;

namespace TokkDb.Core.Pages;

public static class PageUtilities {
  public static long GetPosition(uint index) {
    return index * TokkConstants.PageSize;
  }
  
  public static long GetPosition(this PageBuffer buffer) {
    return GetPosition(buffer.Index);
  }
  
  public static int LoadFields(this IEnumerable<IPageField> fields, PageBuffer buffer, int startPosition) {
    var position = startPosition;
    foreach (var field in fields) {
      var size = field.Read(buffer, position);
      position += size;
    }
    return position;
  }
  
  public static int SaveFields(this IEnumerable<IPageField> fields, PageBuffer buffer, int startPosition) {
    var position = startPosition;
    foreach (var field in fields) {
      var size = field.Write(buffer, position);
      position += size;
    }
    return position;
  }
}
