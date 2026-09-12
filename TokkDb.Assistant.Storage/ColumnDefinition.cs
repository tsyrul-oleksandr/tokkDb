namespace TokkDb.Assistant.Storage;

/// <summary>
/// One column of a collection, described logically.
///
/// Nothing here says where the column's values live, whether they are indexed, or what they cost
/// to read. SC-2 forbids that for the collection definition, and the prohibition is about the
/// whole graph reachable from it, so it applies here too. Whether an index exists is the
/// engine's decision and the planner's business (D-2); saying so in the schema would be the
/// application telling the database how to do its job on the strength of a guess a model made.
///
/// A definition that exists is one that makes sense on its own: its name is a name, and its
/// default value is a value of its type. Anything needing the rest of the schema to judge is
/// judged by <see cref="IStorage"/>.
/// </summary>
public sealed record ColumnDefinition
{
    /// <param name="name">Trimmed and checked as <see cref="StorageNames"/> describes.</param>
    /// <param name="type">The logical type. SC-3: values of it are stored as themselves.</param>
    /// <param name="purpose">
    /// One sentence about what the column keeps, in the user's words rather than the schema's.
    /// The browser shows this (BR-3) and the mapping step reads it, which is why it is part of
    /// the definition and not a comment.
    /// </param>
    /// <param name="required">A write must supply a value, unless <paramref name="defaultValue"/> supplies one.</param>
    /// <param name="unique">
    /// No two records may hold the same value here. SC-4: a second one is refused with a
    /// <see cref="DuplicateValue"/> naming this column and the record that already holds it.
    /// </param>
    /// <param name="readOnly">Written when the record is created and refused on every update.</param>
    /// <param name="defaultValue">
    /// Used when a write leaves the column out. Checked against <paramref name="type"/> here, so
    /// a definition can never carry a default the storage would later refuse.
    /// </param>
    public ColumnDefinition(
        string name,
        ColumnType type,
        string? purpose = null,
        bool required = false,
        bool unique = false,
        bool readOnly = false,
        object? defaultValue = null)
    {
        Name = StorageNames.Normalise(name, "column name");

        if (!Enum.IsDefined(type))
        {
            throw new InvalidDefinitionException("column type", $"Column '{Name}' has no usable type.");
        }

        Type = type;
        Purpose = Sentence(purpose);
        Required = required;
        Unique = unique;
        ReadOnly = readOnly;

        if (!ColumnTypes.TryCanonicalise(type, defaultValue, out var canonical))
        {
            throw new InvalidDefinitionException(
                "default value",
                $"Column '{Name}' keeps {type}, and its default {ColumnTypes.Render(defaultValue)} " +
                $"is {ColumnTypes.Describe(defaultValue)}.");
        }

        DefaultValue = canonical;
    }

    public string Name { get; }

    /// <summary>One sentence about what this column keeps, or null. Never a type name.</summary>
    public string? Purpose { get; }

    public ColumnType Type { get; }

    public bool Required { get; }

    public bool Unique { get; }

    public bool ReadOnly { get; }

    /// <summary>The value a write that leaves this column out gets, already of the column's type.</summary>
    public object? DefaultValue { get; }

    internal static string? Sentence(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
