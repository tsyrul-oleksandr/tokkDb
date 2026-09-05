namespace TokkDb.LLM.Storage.Tests;

public sealed class MemoryStorageContractTests : StorageContractTests
{
    protected override IStorage CreateStorage() => new MemoryStorage();

    protected override bool ValidatesRecordFields => true;

    protected override bool AppliesColumnDefaults => true;

    protected override bool KeepsCollectionMetadata => true;

    protected override bool CollectionNamesIgnoreCase => true;

    protected override Type DuplicateCollectionExceptionType => typeof(InvalidOperationException);

    protected override bool StoresEveryColumnType => true;

    protected override bool GetAllOrderSurvivesADelete => false;

    protected override Type UniqueViolationExceptionType => typeof(StorageValidationException);
}
