namespace TokkDb.Disk;

public class ReadOnlyDatabaseException : Exception {
  public string DatabaseFilePath { get; }

  public ReadOnlyDatabaseException(string databaseFilePath, Exception inner = null)
      : base($"'{databaseFilePath}' is open for reading only and cannot be written to.", inner) {
    DatabaseFilePath = databaseFilePath;
  }
}
