namespace TokkDb.Assistant.Storage;

/// <summary>
/// One record: its identity, the collection it belongs to, and its values.
///
/// The values are already of their columns' types (SC-3). Reading a record never parses
/// anything: a <see cref="ColumnType.Decimal"/> column hands back a <see cref="decimal"/>, a
/// <see cref="ColumnType.Date"/> column hands back a <see cref="DateOnly"/>, and a value that
/// was not one of those never got in.
///
/// <b>With one exception, which is stated rather than hidden.</b> A column that was retyped can
/// leave a stored value with no meaning under the new type. SC-6b says such a value is kept,
/// flagged and surfaced - never dropped and never allowed to make the record unreadable - so it
/// comes back in the type it was recorded as, and <see cref="NeedsAttention"/> names the column
/// it is in. Everything else about the record reads normally, which is the point: one unreadable
/// cost does not cost you the record it is in.
///
/// The identity is a <see cref="Ulid"/> and it is not a column. SC-4 settles both halves: see
/// <see cref="IStorage"/> for why.
/// </summary>
public sealed record StorageRecord
{
    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(StorageNames.Comparer);

    /// <summary>
    /// Builds a record. Callers normally get records from <see cref="IStorage"/> rather than
    /// building them; this exists for the update path, where a record read, changed and handed
    /// back is the whole of it, and for implementations of the contract.
    /// </summary>
    /// <param name="needsAttention">
    /// The columns whose value is not of the column's type, because the column was retyped after
    /// the value was written and the value has no meaning under the new type (SC-6b). Empty for
    /// every record a caller builds, and for almost every record a storage returns.
    /// </param>
    public StorageRecord(
        Ulid id,
        string collectionName,
        IReadOnlyDictionary<string, object?> fields,
        IReadOnlyCollection<string>? needsAttention = null)
    {
        ArgumentNullException.ThrowIfNull(fields);

        Id = id;
        CollectionName = StorageNames.Normalise(collectionName, "collection name");
        Fields = new Dictionary<string, object?>(fields, StorageNames.Comparer);
        NeedsAttention = needsAttention is null or { Count: 0 }
            ? Nothing
            : new HashSet<string>(needsAttention, StorageNames.Comparer);
    }

    /// <summary>
    /// The record's identity, assigned by the storage when the record was created.
    ///
    /// A <see cref="Ulid"/> rather than a sequence number, because it is allocated without
    /// asking the storage anything, which is what makes SC-5's batch possible: five hundred
    /// records can be given their identities before the first write, and a trace step can refer
    /// to a record it is creating in the same transaction (TR-4). It sorts by creation time,
    /// which is a free answer to "what did I add last" and is why <c>GetAll</c> can still
    /// promise nothing about order without the application being left with no way to sort.
    /// </summary>
    public Ulid Id { get; }

    public string CollectionName { get; }

    /// <summary>
    /// The values, by column name. A column the record has no value for is absent rather than
    /// present and null, so that "never given" and "given as nothing" do not become the same
    /// thing the first time someone clears a field.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields { get; }

    /// <summary>
    /// The columns holding a value that is not of the column's type (SC-6b). Empty for almost
    /// every record; a column named here holds its value in the type it was recorded as, and the
    /// browser shows it as needing attention rather than hiding it or showing it as empty.
    /// </summary>
    public IReadOnlySet<string> NeedsAttention { get; }

    /// <summary>Whether any of this record's values is waiting to be made sense of.</summary>
    public bool IsPending => NeedsAttention.Count > 0;

    /// <summary>
    /// Two records are the same record when they have the same identity, belong to the same
    /// collection, and hold the same values for the same columns. Written out for the reason
    /// <see cref="CollectionDefinition.Equals(CollectionDefinition)"/> gives: a record would
    /// compare <see cref="Fields"/> by reference, and then a record read back would never equal
    /// the one that was written.
    /// </summary>
    public bool Equals(StorageRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        if (Id != other.Id || !StorageNames.Same(CollectionName, other.CollectionName)) return false;
        if (Fields.Count != other.Fields.Count) return false;
        if (NeedsAttention.Count != other.NeedsAttention.Count) return false;

        foreach (var (column, value) in Fields)
        {
            if (!other.Fields.TryGetValue(column, out var theirs)) return false;
            if (!Equals(value, theirs)) return false;
        }

        foreach (var column in NeedsAttention)
        {
            if (!other.NeedsAttention.Contains(column)) return false;
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Id, CollectionName, Fields.Count);

    /// <summary>The value of a column, or null if the record has none.</summary>
    public object? this[string columnName] =>
        Fields.TryGetValue(columnName, out var value) ? value : null;

    public bool Has(string columnName) => Fields.ContainsKey(columnName);

    /// <summary>
    /// This record with one column changed. The value is not checked here - checking it needs
    /// the schema, which is why SC-4's second answer puts validation at the write.
    ///
    /// A column that was needing attention stops needing it: whatever was there has just been
    /// replaced by whatever the caller decided it should be.
    /// </summary>
    public StorageRecord With(string columnName, object? value)
    {
        var name = StorageNames.Normalise(columnName, "column name");

        var fields = new Dictionary<string, object?>(Fields, StorageNames.Comparer)
        {
            [name] = value
        };

        return new StorageRecord(Id, CollectionName, fields, Except(name));
    }

    /// <summary>This record with one column cleared, as distinct from set to nothing.</summary>
    public StorageRecord Without(string columnName)
    {
        var name = StorageNames.Normalise(columnName, "column name");
        var fields = new Dictionary<string, object?>(Fields, StorageNames.Comparer);
        fields.Remove(name);

        return new StorageRecord(Id, CollectionName, fields, Except(name));
    }

    private IReadOnlyCollection<string>? Except(string columnName)
    {
        if (NeedsAttention.Count == 0 || !NeedsAttention.Contains(columnName))
        {
            return NeedsAttention.Count == 0 ? null : NeedsAttention.ToArray();
        }

        return NeedsAttention.Where(name => !StorageNames.Same(name, columnName)).ToArray();
    }
}
