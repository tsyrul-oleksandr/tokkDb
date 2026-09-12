using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The contract suite against the engine-backed implementation.
///
/// This is the point of 1.2's shape: the same suite, one factory, a second storage. If the two
/// ever answer the same question differently - what a name means, whether an absent value is a
/// duplicate, whether a refused write left something behind - the disagreement fails here rather
/// than in the application months later, which is the finding §2.2 of the engine plan recorded.
///
/// Each test gets its own database, in a directory of its own that goes afterwards, because a
/// <c>TokkDbConnection</c> holds the file open for as long as it lives and a second writer on
/// the same file is refused.
/// </summary>
public sealed class TokkDbStorageContractTests : StorageContractTests
{
    private readonly List<TemporaryDatabase> _databases = [];

    /// <summary>A unique column is enforced by an index, so this storage has one to seek.</summary>
    protected override bool SeeksIndexes => true;

    protected override IStorage NewStorage()
    {
        var database = new TemporaryDatabase("contract");
        _databases.Add(database);
        return new TokkDbStorage(database.FilePath);
    }

    protected override void Dispose(bool disposing)
    {
        // The storages first: a connection holds its file open for as long as it lives, and the
        // directory cannot go while it does.
        base.Dispose(disposing);

        foreach (var database in _databases) database.Dispose();
        _databases.Clear();
    }
}
