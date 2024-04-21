using TokkDb.Core;
using TokkDb.Data.Collections;
using TokkDb.Disk;
using TokkDb.Options;
using TokkDb.Pages;

namespace TokkDb;

public class TokkDatabase : IDisposable {
  internal TokkDatabaseOptions Options { get; }
  public PageManager PageManager { get; }
  protected bool IsOpen = false;

  public TokkDatabase(TokkDatabaseOptions options, PageManager pageManager) {
    Options = options;
    PageManager = pageManager;
  }

  public virtual ITokkCollection<T> GetCollection<T>(string name) where T : class, new() {
    ValidateOpenConnection();
    return PageManager.GetCollection<T>(name);
  }

  public virtual void Open() {
    if (IsOpen) {
      return;
    }
    Load();
    IsOpen = true;
  }
  
  public virtual void Dispose() {
    if (!IsOpen) {
      return;
    }
  }
  
  protected virtual void Load() {
    PageManager.Load();
  }
  
  protected virtual void ValidateOpenConnection() {
    if (!IsOpen) {
      throw new Exception("Database is not open");
    }
  }
}
