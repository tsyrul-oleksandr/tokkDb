namespace TokkDb.Assistant.Tests;

/// <summary>
/// A database file in a directory of its own, and the directory deleted afterwards.
///
/// A directory rather than a file, because the engine writes more than one: the database, a
/// write lock beside it, and a journal. Naming them here means guessing at the engine's
/// suffixes, and a guess that is wrong leaves files behind silently - which is what the first
/// version of this did, at two files per test.
///
/// A connection holds its file open for as long as it lives, so whoever owns this disposes the
/// storage first.
/// </summary>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory;

    public TemporaryDatabase(string name)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tokkdb-assistant-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "storage.db");
    }

    /// <summary>Where the database goes. Nothing has created it yet.</summary>
    public string FilePath { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Something still has a handle on it. The temporary directory is where it is.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
