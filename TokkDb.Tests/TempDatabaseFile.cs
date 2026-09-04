namespace TokkDb.Tests;

public sealed class TempDatabaseFile : IDisposable {
  public string Path { get; }

  public TempDatabaseFile() {
    Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tokkdb-test-{Guid.NewGuid():N}.db");
  }

  public long Length => new FileInfo(Path).Length;
  public long PageCount => Length / Configuration.TokkConstants.PageSize;

  public void Dispose() {
    if (File.Exists(Path)) {
      File.Delete(Path);
    }
  }
}
