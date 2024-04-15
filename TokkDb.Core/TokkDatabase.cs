using TokkDb.Core.Options;

namespace TokkDb.Core;

public class TokkDatabase : IDisposable {
  internal TokkDatabaseOptions Options { get; }

  public TokkDatabase(string filePath) : this(new TokkDatabaseOptions { FilePath = filePath }) { }

  public TokkDatabase(TokkDatabaseOptions options) {
    Options = options;
  }
  
  public virtual void Open() {
    
  }

  public virtual void Dispose() {
    // TODO release managed resources here
  }
}
