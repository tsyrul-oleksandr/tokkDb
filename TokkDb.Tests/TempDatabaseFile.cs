namespace TokkDb.Tests;

public sealed class TempDatabaseFile : IDisposable {
  public string Path { get; }

  public TempDatabaseFile() {
    Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tokkdb-test-{Guid.NewGuid():N}.db");
  }

  public long Length => new FileInfo(Path).Length;
  public long PageCount => Length / Configuration.TokkConstants.DefaultPageSize;

  public void Dispose() {
    Delete(Path);
    //The journal lives beside the database file and goes with it.
    Delete(Disk.Journal.GetJournalPath(Path));
  }

  private static void Delete(string path) {
    if (File.Exists(path)) {
      File.Delete(path);
    }
  }
}
