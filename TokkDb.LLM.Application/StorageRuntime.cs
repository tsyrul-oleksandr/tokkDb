using TokkDb.LLM.Storage;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Application;

/// <summary>
/// Which storage the application is using.
///
/// TokkDb is the default. FileStorage used to be the other option and is gone: every one of
/// its members threw <c>NotImplementedException</c>, so it was a backend in the enum and
/// nothing anywhere else.
/// </summary>
public sealed class StorageRuntime : IStorageRuntime
{
    private readonly MemoryStorage _memoryStorage;
    private readonly TokkDbStorage _tokkDbStorage;

    public StorageRuntime(MemoryStorage memoryStorage, TokkDbStorage tokkDbStorage)
    {
        _memoryStorage = memoryStorage;
        _tokkDbStorage = tokkDbStorage;
        CurrentBackend = Settings.Settings.Instance.StorageType;
    }

    public StorageBackend CurrentBackend { get; private set; }

    public IStorage Storage =>
        Settings.Settings.Instance.StorageType == StorageBackend.Memory
            ? _memoryStorage
            : _tokkDbStorage;

    public IReadOnlyCollection<StorageBackend> Backends { get; } =
        [StorageBackend.TokkDb, StorageBackend.Memory];

    public void SwitchBackend(StorageBackend backend)
    {
        Settings.Settings.Instance.StorageType = backend;
        CurrentBackend = backend;
    }
}
