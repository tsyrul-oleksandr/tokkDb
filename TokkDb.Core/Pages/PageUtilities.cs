namespace TokkDb.Core.Pages;

public static class PageUtilities {
  public static long GetPosition(uint index) {
    return index * TokkConstants.PageSize;
  }
  
  public static long GetPosition(this PageBuffer buffer) {
    return GetPosition(buffer.Index);
  }
}
