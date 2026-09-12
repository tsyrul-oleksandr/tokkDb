namespace TokkDb.Assistant.Storage;

/// <summary>
/// A thing the storage keeps, described logically: what it is called, what it is for, what it
/// keeps, what the application wants to remember about it, and how one of its records reads.
///
/// SC-2, in full. The five properties below are all of it. There is no page number, no chain
/// pointer, no index root and no record count here, and none is reachable from anything here
/// either: not from a column, not from the display rule. That is not a coincidence to be
/// maintained by care - <c>ContractShapeTests</c> walks the whole graph reachable from this type
/// and fails if anything outside the contract's own vocabulary appears in it.
///
/// The reason is the one D-1 gives. A definition that can reach a page number is a definition
/// that can be built only by something that knows about pages, and then the classifier, the
/// mapping step, the browser and the in-memory fake all have to know about pages too. A count
/// is the subtler one: a definition carrying <c>RecordCount</c> is a definition whose truth
/// expires, and every copy of it anywhere becomes a stale number someone will believe.
/// </summary>
public sealed record CollectionDefinition
{
    /// <param name="name">Trimmed and checked as <see cref="StorageNames"/> describes.</param>
    /// <param name="purpose">
    /// One sentence saying what this collection keeps, in the user's words. BR-1 shows it beside
    /// the name, and the mapping step reads it to decide whether new data belongs here, so it is
    /// load-bearing rather than decorative.
    /// </param>
    /// <param name="columns">
    /// The columns, in the order they should be shown. Two columns cannot share a name; names
    /// are compared as <see cref="StorageNames"/> describes.
    /// </param>
    /// <param name="metadata">
    /// What the application wants to remember about this collection that is not part of its
    /// shape: where the data came from, when it was last written to, what the user calls it in
    /// their own words. Free-form on purpose - the alternative is a new property on this type
    /// every time the application learns something new, and this type is the one SC-2 pins down.
    /// </param>
    /// <param name="displayRule">How one record reads as a line of text, or null for none.</param>
    public CollectionDefinition(
        string name,
        string? purpose = null,
        IReadOnlyList<ColumnDefinition>? columns = null,
        IReadOnlyDictionary<string, string?>? metadata = null,
        DisplayRule? displayRule = null)
    {
        Name = StorageNames.Normalise(name, "collection name");
        Purpose = ColumnDefinition.Sentence(purpose);
        Columns = Distinct(Name, columns ?? []);
        Metadata = metadata is null
            ? EmptyMetadata
            : new Dictionary<string, string?>(metadata, StorageNames.Comparer);
        DisplayRule = displayRule;
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyMetadata =
        new Dictionary<string, string?>(StorageNames.Comparer);

    public string Name { get; }

    /// <summary>One sentence saying what this collection keeps, or null.</summary>
    public string? Purpose { get; }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public IReadOnlyDictionary<string, string?> Metadata { get; }

    /// <summary>How one record of this collection reads as a line of text, or null.</summary>
    public DisplayRule? DisplayRule { get; }

    /// <summary>The column by that name, or null. Names are compared as <see cref="StorageNames"/> describes.</summary>
    public ColumnDefinition? Column(string columnName)
    {
        foreach (var column in Columns)
        {
            if (StorageNames.Same(column.Name, columnName)) return column;
        }

        return null;
    }

    /// <summary>
    /// Two definitions are the same definition when they say the same thing: the same name and
    /// purpose, the same columns in the same order, the same metadata, the same display rule.
    ///
    /// Written out because a record would compare <see cref="Columns"/> and
    /// <see cref="Metadata"/> by reference, and then a definition read back from a storage would
    /// never equal the one that was written - which is exactly the comparison the contract suite
    /// and "has anything about this collection changed?" both want to make.
    /// </summary>
    public bool Equals(CollectionDefinition? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return StorageNames.Same(Name, other.Name)
               && string.Equals(Purpose, other.Purpose, StringComparison.Ordinal)
               && DisplayRule == other.DisplayRule
               && Columns.SequenceEqual(other.Columns)
               && SameMetadata(Metadata, other.Metadata);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Purpose, StringComparer.Ordinal);
        hash.Add(DisplayRule);
        hash.Add(Columns.Count);
        hash.Add(Metadata.Count);
        return hash.ToHashCode();
    }

    private static bool SameMetadata(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right)
    {
        if (left.Count != right.Count) return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other)) return false;
            if (!string.Equals(value, other, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>This definition with a different display rule.</summary>
    public CollectionDefinition WithDisplayRule(DisplayRule? displayRule) =>
        new(Name, Purpose, Columns, Metadata, displayRule);

    /// <summary>This definition with a different set of columns.</summary>
    public CollectionDefinition WithColumns(IReadOnlyList<ColumnDefinition> columns) =>
        new(Name, Purpose, columns, Metadata, DisplayRule);

    /// <summary>This definition with different metadata.</summary>
    public CollectionDefinition WithMetadata(IReadOnlyDictionary<string, string?> metadata) =>
        new(Name, Purpose, Columns, metadata, DisplayRule);

    private static IReadOnlyList<ColumnDefinition> Distinct(
        string collectionName,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var seen = new HashSet<string>(StorageNames.Comparer);
        var kept = new List<ColumnDefinition>(columns.Count);

        foreach (var column in columns)
        {
            if (column is null)
            {
                throw new InvalidDefinitionException(
                    "column",
                    $"'{collectionName}' was given a column that is not there.");
            }

            if (!seen.Add(column.Name))
            {
                throw new InvalidDefinitionException(
                    "column name",
                    $"'{collectionName}' was given two columns called '{column.Name}'.");
            }

            kept.Add(column);
        }

        return kept;
    }
}
