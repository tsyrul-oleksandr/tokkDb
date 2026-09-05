namespace TokkDb.Disk;

//TX-4: the second writer is turned away rather than allowed to interleave its pages with
//the first one's.
public class DatabaseLockedException : Exception {
  public string DatabaseFilePath { get; }

  public DatabaseLockedException(string databaseFilePath, Exception inner = null)
      : base($"'{databaseFilePath}' is already open for writing. TokkDb allows one writer at a time; " +
        $"open the database with {nameof(TokkDbAccessMode)}.{nameof(TokkDbAccessMode.ReadOnly)} to read " +
        $"it alongside the writer.", inner) {
    DatabaseFilePath = databaseFilePath;
  }
}
