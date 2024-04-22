using TokkDb.Core.Pages;

namespace TokkDb.Disk.Cache;

public class PageMemoryCache {
  protected Dictionary<uint, PageBuffer> Pages { get; } = new();
  
  public PageBuffer? Get(uint index) {
    return Pages.GetValueOrDefault(index);
  }
  
  public void Set(uint index, PageBuffer page) {
    Pages[index] = page;
  }
}
