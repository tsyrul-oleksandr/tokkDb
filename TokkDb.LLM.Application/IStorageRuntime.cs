using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application;

public interface IStorageRuntime
{
    IStorage Storage { get; }

    IReadOnlyCollection<StorageBackend> Backends { get; }

    void SwitchBackend(StorageBackend backend);
}
