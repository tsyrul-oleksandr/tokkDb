using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The contract suite against <see cref="MemoryStorage"/>.
///
/// This is the whole of what an implementation has to write to be held to the contract. The
/// engine-backed one (1.3) will be the same four lines with a database file behind them.
/// </summary>
public sealed class MemoryStorageContractTests : StorageContractTests
{
    protected override IStorage NewStorage() => new MemoryStorage();
}
