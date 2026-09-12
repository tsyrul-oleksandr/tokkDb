namespace TokkDb.Assistant.Storage;

/// <summary>
/// The rules both implementations of <see cref="IStorage"/> obey, in one place.
///
/// This exists because of what §2.2 of the engine plan recorded: two storage backends drift
/// apart on exactly the questions nobody wrote down. Leaving "is an absent value a duplicate?"
/// or "does a default apply on update?" to each implementation guarantees two answers, and the
/// contract suite would then be testing two contracts. So the judgement lives here and the
/// implementations supply only what they alone can know - how to find the record that holds a
/// value, which is a scan in memory and an index seek on the engine.
///
/// Internal on purpose. These are not decisions a caller gets to make.
/// </summary>
internal static class SharedRules
{
    /// <summary>
    /// The check a definition cannot make alone: that its display rule names columns it has.
    /// Everything a definition can check about itself was checked when it was built.
    /// </summary>
    public static void CheckDefinitionFitsItself(CollectionDefinition definition)
    {
        if (definition.DisplayRule is null) return;

        foreach (var reference in definition.DisplayRule.ColumnReferences)
        {
            if (definition.Column(reference) is null)
            {
                throw new InvalidDefinitionException(
                    "display rule",
                    $"The display rule of '{definition.Name}' names a column '{reference}' that it does not have.");
            }
        }
    }

    /// <summary>
    /// Judges the values offered for a new record and returns them as the record will hold them.
    /// </summary>
    /// <param name="definition">The collection the record is going into.</param>
    /// <param name="offered">The values the caller offered.</param>
    /// <param name="holderOf">
    /// Given a unique column and a canonical value, the record that already holds it, or null.
    /// </param>
    /// <exception cref="StorageValidationException">Thrown with every reason, not only the first.</exception>
    public static Dictionary<string, object?> ForCreate(
        CollectionDefinition definition,
        IReadOnlyDictionary<string, object?> offered,
        Func<ColumnDefinition, object, Ulid?> holderOf) =>
        Judge(definition, offered, existing: null, holderOf);

    /// <summary>
    /// Judges the values an update carries, against the record it would replace.
    /// </summary>
    /// <exception cref="StorageValidationException">Thrown with every reason, not only the first.</exception>
    public static Dictionary<string, object?> ForUpdate(
        CollectionDefinition definition,
        StorageRecord offered,
        StorageRecord existing,
        Func<ColumnDefinition, object, Ulid?> holderOf) =>
        Judge(definition, offered.Fields, existing, holderOf);

    private static Dictionary<string, object?> Judge(
        CollectionDefinition definition,
        IReadOnlyDictionary<string, object?> offered,
        StorageRecord? existing,
        Func<ColumnDefinition, object, Ulid?> holderOf)
    {
        var errors = new List<StorageError>();
        var judged = new Dictionary<string, object?>(StorageNames.Comparer);

        // A column the collection does not have is reported before anything else, because a
        // model that invented a column usually also left out the one it meant, and the reply it
        // needs names both.
        foreach (var name in offered.Keys)
        {
            if (definition.Column(name) is null)
            {
                errors.Add(new UnknownColumn(definition.Name, name));
            }
        }

        foreach (var column in definition.Columns)
        {
            if (!offered.TryGetValue(column.Name, out var raw))
            {
                JudgeAbsent(definition, column, existing, judged, errors);
                continue;
            }

            if (!ColumnTypes.TryCanonicalise(column.Type, raw, out var value))
            {
                errors.Add(new ColumnTypeMismatch(definition.Name, column.Name, column.Type, raw));
                continue;
            }

            // Offered as nothing is not the same as left out. A column that has to have a value
            // cannot be given nothing, and the default does not step in: the caller said
            // nothing, not "whatever you like".
            if (value is null && column.Required)
            {
                errors.Add(new RequiredValueMissing(definition.Name, column.Name));
                continue;
            }

            if (existing is not null && column.ReadOnly && !Equals(existing[column.Name], value))
            {
                errors.Add(new ReadOnlyColumnChanged(definition.Name, column.Name, existing[column.Name], value));
                continue;
            }

            if (column.Unique && value is not null && holderOf(column, value) is { } holder &&
                (existing is null || holder != existing.Id))
            {
                errors.Add(new DuplicateValue(definition.Name, column.Name, value, holder));
                continue;
            }

            judged[column.Name] = value;
        }

        if (errors.Count > 0)
        {
            throw new StorageValidationException(errors);
        }

        return judged;
    }

    private static void JudgeAbsent(
        CollectionDefinition definition,
        ColumnDefinition column,
        StorageRecord? existing,
        Dictionary<string, object?> judged,
        List<StorageError> errors)
    {
        if (existing is null)
        {
            // Creating. A default is what a record starts with.
            if (column.DefaultValue is not null)
            {
                judged[column.Name] = column.DefaultValue;
                return;
            }

            if (column.Required)
            {
                errors.Add(new RequiredValueMissing(definition.Name, column.Name));
            }

            return;
        }

        // Updating. An update carries the whole record, so a column left out is a column
        // cleared - and the default does not come back. A default is what a record starts with,
        // not what it returns to: applying it again would resurrect a value the caller had
        // deliberately removed, and there would be no way to clear such a column at all.
        if (column.ReadOnly && existing[column.Name] is not null)
        {
            errors.Add(new ReadOnlyColumnChanged(definition.Name, column.Name, existing[column.Name], null));
        }

        if (column.Required)
        {
            errors.Add(new RequiredValueMissing(definition.Name, column.Name));
        }
    }
}
