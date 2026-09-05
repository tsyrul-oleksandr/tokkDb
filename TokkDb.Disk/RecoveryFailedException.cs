namespace TokkDb.Disk;

//Recovery fails closed. If the journal cannot be read, whether the database file holds a
//half applied transaction cannot be established, so the database is not opened.
public class RecoveryFailedException : Exception {
  public string DatabaseFilePath { get; }

  public RecoveryFailedException(string databaseFilePath, string reason, Exception inner = null)
      : base($"'{databaseFilePath}' cannot be opened: {reason}", inner) {
    DatabaseFilePath = databaseFilePath;
  }
}
