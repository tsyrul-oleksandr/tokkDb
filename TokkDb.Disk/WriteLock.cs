namespace TokkDb.Disk;

//The single writer token. It is a file beside the database opened so that nothing else may
//share it, which is the one form of exclusion that behaves the same on every platform this
//runs on.
public sealed class WriteLock : IDisposable {
  public const string FileExtension = ".lock";

  private readonly FileStream _stream;
  private bool _disposed;

  public string FilePath { get; }

  public WriteLock(string databaseFilePath) {
    FilePath = GetLockPath(databaseFilePath);
    try {
      _stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new DatabaseLockedException(databaseFilePath, exception);
    }
  }

  public static string GetLockPath(string databaseFilePath) {
    return databaseFilePath + FileExtension;
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _stream.Dispose();
  }
}
