using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application.Databases;

public sealed class RecordViewModel
{
    public RecordViewModel(
        StorageRecord record,
        string summary)
    {
        Record = record;
        Summary = summary;
    }

    public StorageRecord Record { get; }

    public string Summary { get; }
}
