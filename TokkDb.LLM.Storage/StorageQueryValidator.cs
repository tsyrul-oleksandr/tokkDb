using System.Globalization;

namespace TokkDb.LLM.Storage;

/// <summary>
/// Whether a bound query fits the schema it names.
///
/// The binder resolved the names, so nothing here asks whether a column exists — it asks
/// whether the pieces go together: the column belonging to the collection actually in scope
/// at that point, the operator suiting its type, the operands converting to it, and the sort
/// and paging being sensible.
///
/// It is separate from any one backend because the answer is the same for all of them: a
/// query that does not fit the schema does not fit it wherever it would have run.
/// <see cref="MemoryStorage"/> checks the same rules inside the compiler that turns a filter
/// into its executable form, so the two are written once each rather than shared — splitting
/// that compiler would mean walking the filter twice to gain nothing it needs.
/// </summary>
public static class StorageQueryValidator
{
    public static IReadOnlyList<StorageValidationError> Validate(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var errors = new List<StorageValidationError>();
        ValidateFilter(query.Where, query.Collection, errors);

        foreach (var sort in query.OrderBy.Where(sort => !BelongsTo(sort.Column, query.Collection)))
        {
            errors.Add(new StorageValidationError(
                "ColumnNotInCollection",
                sort.Column.Name,
                $"Sort column '{sort.Column.Name}' does not belong to collection '{query.Collection.Name}'."));
        }

        foreach (var column in query.Select.Where(column => !BelongsTo(column, query.Collection)))
        {
            errors.Add(new StorageValidationError(
                "ColumnNotInCollection",
                column.Name,
                $"Selected column '{column.Name}' does not belong to collection '{query.Collection.Name}'."));
        }

        if (query.Skip < 0)
        {
            errors.Add(new StorageValidationError("InvalidSkip", null, "skip must be zero or greater."));
        }

        if (query.Take < 1)
        {
            errors.Add(new StorageValidationError("InvalidTake", null, "take must be greater than zero."));
        }

        return errors;
    }

    /// <summary>Validates and throws, for a caller that has nothing to do with the errors but report them.</summary>
    public static void ThrowIfInvalid(StorageQuery query)
    {
        var errors = Validate(query);
        if (errors.Count > 0)
        {
            throw new StorageValidationException(errors);
        }
    }

    private static void ValidateFilter(
        StorageFilter? filter,
        CollectionDefinition scope,
        List<StorageValidationError> errors)
    {
        switch (filter)
        {
            case null:
                return;

            case StorageGroupFilter group:
                foreach (var child in group.Filters)
                {
                    ValidateFilter(child, scope, errors);
                }

                return;

            case StorageNotFilter not:
                ValidateFilter(not.Inner, scope, errors);
                return;

            case StorageRelationFilter relation:
                ValidateRelation(relation, scope, errors);
                return;

            case StorageFieldFilter field:
                ValidateField(field, scope, errors);
                return;

            default:
                errors.Add(new StorageValidationError("UnknownFilter", null, "Unsupported condition."));
                return;
        }
    }

    // The scope changes as a relation step is followed, which is what makes a column borrowed
    // from the wrong collection detectable at all.
    private static void ValidateRelation(
        StorageRelationFilter relation,
        CollectionDefinition scope,
        List<StorageValidationError> errors)
    {
        if (!BelongsTo(relation.SourceColumn, scope))
        {
            errors.Add(new StorageValidationError(
                "ColumnNotInCollection",
                relation.SourceColumn.Name,
                $"Column '{relation.SourceColumn.Name}' does not belong to collection '{scope.Name}'."));
            return;
        }

        if (!BelongsTo(relation.TargetColumn, relation.TargetCollection))
        {
            errors.Add(new StorageValidationError(
                "ColumnNotInCollection",
                relation.TargetColumn.Name,
                $"Column '{relation.TargetColumn.Name}' does not belong to collection '{relation.TargetCollection.Name}'."));
            return;
        }

        ValidateFilter(relation.Inner, relation.TargetCollection, errors);
    }

    private static void ValidateField(
        StorageFieldFilter filter,
        CollectionDefinition scope,
        List<StorageValidationError> errors)
    {
        var column = filter.Column;

        if (!BelongsTo(column, scope))
        {
            errors.Add(new StorageValidationError(
                "ColumnNotInCollection",
                column.Name,
                $"Column '{column.Name}' does not belong to collection '{scope.Name}'."));
            return;
        }

        if (!IsOperatorAllowed(filter.Operator, column.Type))
        {
            errors.Add(new StorageValidationError(
                "OperatorNotAllowed",
                column.Name,
                $"Operator '{filter.Operator}' cannot be used on column '{column.Name}' of type {column.Type}."));
            return;
        }

        foreach (var operand in filter.Operands.Where(operand => !ConvertsTo(operand, column.Type)))
        {
            errors.Add(new StorageValidationError(
                "InvalidOperandType",
                column.Name,
                $"Value '{operand}' is not a valid {column.Type} for column '{column.Name}'."));
            return;
        }
    }

    private static bool BelongsTo(ColumnDefinition column, CollectionDefinition collection) =>
        collection.Columns.Any(candidate =>
            string.Equals(candidate.Name, column.Name, StringComparison.OrdinalIgnoreCase) &&
            candidate.Type == column.Type);

    private static bool IsOperatorAllowed(QueryOperator op, Core.ColumnType type) =>
        op switch
        {
            QueryOperator.Equals or QueryOperator.NotEquals or QueryOperator.In or
                QueryOperator.IsNull or QueryOperator.IsNotNull => true,

            QueryOperator.StartsWith or QueryOperator.EndsWith or QueryOperator.Contains =>
                type == Core.ColumnType.String,

            QueryOperator.GreaterThan or QueryOperator.GreaterOrEqual or QueryOperator.LessThan or
                QueryOperator.LessOrEqual or QueryOperator.Between =>
                type is Core.ColumnType.Int32 or Core.ColumnType.Int64 or Core.ColumnType.Decimal
                    or Core.ColumnType.DateTime,

            _ => false
        };

    private static bool ConvertsTo(string? operand, Core.ColumnType type)
    {
        if (operand is null)
        {
            return true;
        }

        var text = operand.Trim();
        return type switch
        {
            Core.ColumnType.String => true,
            Core.ColumnType.Boolean => bool.TryParse(text, out _),
            Core.ColumnType.Int32 => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            Core.ColumnType.Int64 => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            Core.ColumnType.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            Core.ColumnType.DateTime => DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            Core.ColumnType.Guid => Guid.TryParse(text, out _),
            _ => true
        };
    }
}
