using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

public sealed class TokkDbStorageContractTests : StorageContractTests
{
    private readonly List<string> _databaseFilePaths = [];

    protected override IStorage CreateStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokkdb-contract-{Ulid.NewUlid()}.db");
        _databaseFilePaths.Add(path);
        return new TokkDbStorage(path);
    }

    protected override bool ValidatesRecordFields => false;

    protected override bool AppliesColumnDefaults => false;

    protected override bool KeepsCollectionMetadata => false;

    protected override bool CollectionNamesIgnoreCase => false;

    protected override Type DuplicateCollectionExceptionType => typeof(ArgumentException);

    protected override bool StoresEveryColumnType => true;

    protected override bool GetAllOrderSurvivesADelete => false;

    public override void Dispose()
    {
        base.Dispose();
        foreach (var path in _databaseFilePaths.SelectMany(path => new[]
                 {
                     path,
                     TokkDb.Disk.Journal.GetJournalPath(path),
                     TokkDb.Disk.WriteLock.GetLockPath(path)
                 }))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
