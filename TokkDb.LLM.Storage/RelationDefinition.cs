namespace TokkDb.LLM.Storage;

public sealed record RelationDefinition
{
    public RelationDefinition(
        string name,
        RelationType type,
        string sourceCollection,
        string sourceColumn,
        string targetCollection,
        string targetColumn,
        string? description = null)
    {
        Name = StorageValidation.NormalizeName(name, "relation name");
        Type = type;
        SourceCollection = StorageValidation.NormalizeName(sourceCollection, "source collection name");
        SourceColumn = StorageValidation.NormalizeName(sourceColumn, "source column name");
        TargetCollection = StorageValidation.NormalizeName(targetCollection, "target collection name");
        TargetColumn = StorageValidation.NormalizeName(targetColumn, "target column name");
        Description = description;
    }

    public string Name { get; }

    public RelationType Type { get; }

    public string SourceCollection { get; }

    public string SourceColumn { get; }

    public string TargetCollection { get; }

    public string TargetColumn { get; }

    public string? Description { get; }
}
