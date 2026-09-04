using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application;

public sealed class StorageRuntime : IStorageRuntime
{
    private readonly MemoryStorage _memoryStorage;
    private readonly FileStorage _fileStorage;

    public StorageRuntime(IServiceProvider provider, MemoryStorage memoryStorage, FileStorage fileStorage)
    {
        _memoryStorage = memoryStorage;
        _fileStorage = fileStorage;
    }

    public StorageBackend CurrentBackend { get; private set; }

    public IStorage Storage => Settings.Settings.Instance.StorageType == StorageBackend.Memory ? _memoryStorage : _fileStorage;

    public IReadOnlyCollection<StorageBackend> Backends { get; } = new[] { StorageBackend.Memory, StorageBackend.File };

    public void SwitchBackend(StorageBackend backend)
    {
        CurrentBackend = backend;
    }
}
