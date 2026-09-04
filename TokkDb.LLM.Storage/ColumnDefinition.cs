namespace TokkDb.LLM.Storage;

public sealed record ColumnDefinition
{
    public ColumnDefinition(
        string name,
        Core.ColumnType type,
        string? description = null,
        bool unique = false,
        bool readOnly = false,
        object? defaultValue = null,
        string? semanticTypeName = null,
        string? validationPattern = null,
        IReadOnlyCollection<string>? validationPatterns = null)
    {
        Name = StorageValidation.NormalizeName(name, "column name");
        Type = type;
        Description = description;
        Unique = unique;
        ReadOnly = readOnly;

        if (defaultValue is not null && !StorageValidation.IsValueCompatible(Type, defaultValue))
        {
            throw new ArgumentException(
                $"Default value for column '{Name}' is incompatible with type '{Type}'.",
                nameof(defaultValue));
        }

        DefaultValue = defaultValue;
        SemanticTypeName = string.IsNullOrWhiteSpace(semanticTypeName)
            ? null
            : semanticTypeName.Trim();
        ValidationPattern = string.IsNullOrWhiteSpace(validationPattern)
            ? null
            : validationPattern.Trim();
        ValidationPatterns = StorageValidation.NormalizeValidationPatterns(ValidationPattern, validationPatterns);
    }

    public string Name { get; }

    public Core.ColumnType Type { get; }

    public string? Description { get; }

    public bool Unique { get; }

    public bool ReadOnly { get; }

    public object? DefaultValue { get; }

    public string? SemanticTypeName { get; }

    public string? ValidationPattern { get; }

    public IReadOnlyCollection<string> ValidationPatterns { get; }
}
