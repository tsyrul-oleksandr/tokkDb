namespace TokkDb.Assistant.Storage;

/// <summary>
/// What each structural change of SC-6 does to a definition, and what it refuses.
///
/// Shared for the same reason <see cref="SharedRules"/> is: both implementations have to agree
/// on what renaming a column means, and the engine-backed one cannot work it out from the
/// engine, which is told what changed rather than diffing two column sets. A rename and a
/// remove-then-add look identical in a diff and mean opposite things to a record written before
/// either, so the caller says which it was and this is where "which it was" is worked out once.
///
/// Each of these produces the definition afterwards. Applying it - to a dictionary, or to the
/// catalogue with a migration step recorded beside it - is the implementation's.
/// </summary>
internal static class StructuralChange
{
    public static CollectionDefinition Add(CollectionDefinition definition, ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (definition.Column(column.Name) is not null)
        {
            throw new ColumnAlreadyExistsException(definition.Name, column.Name);
        }

        // Additive, so D-7 lets it happen without asking. Records that already exist gain no
        // value for it - not even its default, which is what a record starts with and these
        // records started without it.
        return definition.WithColumns([.. definition.Columns, column]);
    }

    public static CollectionDefinition Rename(CollectionDefinition definition, string columnName, string newName)
    {
        var from = Require(definition, columnName);
        var to = StorageNames.Normalise(newName, "column name");

        if (StorageNames.Same(from.Name, to))
        {
            return definition;
        }

        if (definition.Column(to) is not null)
        {
            throw new ColumnAlreadyExistsException(definition.Name, to);
        }

        var renamed = new ColumnDefinition(
            to, from.Type, from.Purpose, from.Required, from.Unique, from.ReadOnly, from.DefaultValue);

        // The display rule names columns, so a rename that left it alone would leave it naming
        // one that no longer exists. Only the references move; literal text that happens to
        // contain the old name is not a reference and is not touched.
        var rule = definition.DisplayRule?.ColumnReferences.Contains(from.Name, StorageNames.Comparer) == true
            ? definition.DisplayRule.WithColumnRenamed(from.Name, to)
            : definition.DisplayRule;

        return definition
            .WithColumns([.. definition.Columns.Select(column => StorageNames.Same(column.Name, from.Name) ? renamed : column)])
            .WithDisplayRule(rule);
    }

    public static CollectionDefinition Retype(CollectionDefinition definition, string columnName, ColumnType newType)
    {
        var before = Require(definition, columnName);

        if (!Enum.IsDefined(newType))
        {
            throw new InvalidDefinitionException("column type", $"Column '{before.Name}' has no usable type.");
        }

        if (before.Type == newType)
        {
            return definition;
        }

        // The declared default is a value of the column, so it is converted by the same rule
        // every stored value is. One that has no meaning under the new type leaves the column
        // without a default, which is the same answer a record gets.
        var moved = new ColumnDefinition(
            before.Name,
            newType,
            before.Purpose,
            before.Required,
            before.Unique,
            before.ReadOnly,
            ColumnConversion.Retype(before.Type, newType, before.DefaultValue));

        return definition.WithColumns(
            [.. definition.Columns.Select(column => StorageNames.Same(column.Name, before.Name) ? moved : column)]);
    }

    public static CollectionDefinition Remove(CollectionDefinition definition, string columnName)
    {
        var column = Require(definition, columnName);

        // Refused rather than silently dropping the rule. Removing a column the collection is
        // displayed by is two decisions, and the caller has to make the second one: D-7 has the
        // application ask before a loss, and it cannot describe a loss it was not told about.
        if (definition.DisplayRule?.ColumnReferences.Contains(column.Name, StorageNames.Comparer) == true)
        {
            throw new InvalidDefinitionException(
                "display rule",
                $"'{column.Name}' cannot be removed from '{definition.Name}' while its display rule " +
                $"'{definition.DisplayRule.Template}' names it.");
        }

        return definition.WithColumns(
            [.. definition.Columns.Where(candidate => !StorageNames.Same(candidate.Name, column.Name))]);
    }

    private static ColumnDefinition Require(CollectionDefinition definition, string columnName)
    {
        var name = StorageNames.Normalise(columnName, "column name");
        return definition.Column(name) ?? throw new UnknownColumnException(definition.Name, name);
    }
}
