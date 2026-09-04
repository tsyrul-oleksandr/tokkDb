namespace TokkDb.LLM.Application.Databases;

public sealed class ColumnViewModel
{
    public ColumnViewModel(
        string name,
        string type,
        string? description,
        bool unique,
        bool readOnly)
    {
        Name = name;
        Type = type;
        Description = description ?? string.Empty;
        Unique = unique;
        ReadOnly = readOnly;
    }

    public string Name { get; }

    public string Type { get; }

    public string Description { get; }

    public bool Unique { get; }

    public bool ReadOnly { get; }
}
