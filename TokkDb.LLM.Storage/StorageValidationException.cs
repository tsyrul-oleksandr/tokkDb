namespace TokkDb.LLM.Storage;

public sealed class StorageValidationException : Exception
{
    public StorageValidationException(IReadOnlyCollection<StorageValidationError> errors)
        : base("Record validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyCollection<StorageValidationError> Errors { get; }
}
