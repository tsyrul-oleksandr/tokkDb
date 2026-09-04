namespace TokkDb.LLM.Storage;

public sealed record CollectionDefinition
{
    public CollectionDefinition(
        string name,
        string? description = null,
        IReadOnlyCollection<ColumnDefinition>? columns = null,
        IReadOnlyDictionary<string, string?>? metadata = null,
        DisplayRule? displayRule = null)
    {
        Name = StorageValidation.NormalizeName(name, "collection name");
        Description = description;
        Columns = ValidateColumns(columns ?? Array.Empty<ColumnDefinition>());
        Metadata = new Dictionary<string, string?>(metadata ?? new Dictionary<string, string?>(), StringComparer.Ordinal);
        DisplayRule = displayRule;
    }

    public string Name { get; }

    public string? Description { get; }

    public IReadOnlyCollection<ColumnDefinition> Columns { get; }

    public IReadOnlyDictionary<string, string?> Metadata { get; }

    /// <summary>
    /// How a record of this collection is rendered as a human-readable value.
    /// Part of the collection definition, not of individual records: the display
    /// value is computed on demand rather than stored per record.
    /// </summary>
    public DisplayRule? DisplayRule { get; }

    /// <summary>Returns a copy of this definition carrying a different display rule.</summary>
    public CollectionDefinition WithDisplayRule(DisplayRule? displayRule) =>
        new(Name, Description, Columns, Metadata, displayRule);

    private static IReadOnlyCollection<ColumnDefinition> ValidateColumns(IReadOnlyCollection<ColumnDefinition> columns)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<ColumnDefinition>(columns.Count);

        foreach (var column in columns)
        {
            if (!names.Add(column.Name))
            {
                throw new InvalidOperationException($"Duplicate column name '{column.Name}' is not allowed.");
            }

            validated.Add(column);
        }

        return validated.AsReadOnly();
    }
}
