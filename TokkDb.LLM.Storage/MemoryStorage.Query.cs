using Microsoft.Extensions.Logging;
using System.Globalization;

namespace TokkDb.LLM.Storage;

/// <summary>
/// Query validation and execution for the in-memory store.
///
/// The definitions in a <see cref="StorageQuery"/> are known to exist - the
/// binder resolved them - so what is checked here is whether they fit together:
/// the column must belong to the collection actually being filtered at that
/// point, the operator must suit its type, and the operands must convert to it.
///
/// The evaluation itself is specific to this implementation. A store that can
/// push filtering down would translate the same <see cref="StorageQuery"/>
/// instead of walking records.
/// </summary>
public sealed partial class MemoryStorage
{
    public StorageQueryResult ExecuteQuery(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_sync)
        {
            var errors = new List<StorageValidationError>();
            var plan = CompileFilter(query.Where, query.Collection, errors);
            ValidateSortAndSelect(query, errors);

            if (errors.Count > 0)
            {
                _logger.LogWarning(
                    "Query validation failed. Collection: {CollectionName}, Errors: {ValidationErrors}",
                    query.Collection.Name,
                    string.Join(" | ", errors.Select(error => error.Message)));
                throw new StorageValidationException(errors);
            }

            var state = GetCollectionState(query.Collection.Name);

            // A restriction by id narrows the candidates before any condition is
            // evaluated, so "this record, if it also matches" costs one lookup
            // rather than a scan.
            var candidates = query.Ids is { Count: > 0 }
                ? query.Ids
                    .Distinct()
                    .Select(id => state.Records.TryGetValue(id, out var record) ? record : null)
                    .Where(record => record is not null)
                    .Select(record => record!)
                : state.Records.Values;

            var matched = candidates
                .Where(record => Matches(record, plan))
                .ToList();

            var rows = ApplyOrdering(matched, query.OrderBy)
                .Skip(query.Skip)
                .Take(query.Take)
                .Select(record => Project(record, query.Select))
                .ToArray();

            _logger.LogInformation(
                "Query executed. Collection: {CollectionName}, Returned: {ReturnedCount}, Skip: {Skip}, Take: {Take}",
                query.Collection.Name,
                rows.Length,
                query.Skip,
                query.Take);

            return new StorageQueryResult(query.Collection.Name, rows, query.Skip, query.Take);
        }
    }

    // =====================================================================
    // Validation: does the resolved query actually fit the schema?
    // =====================================================================

    /// <summary>
    /// Checks a filter against the collection in scope and converts its operands
    /// to the column's type, producing the executable form. The scope changes as
    /// relation steps are followed, which is what makes a column borrowed from
    /// another collection detectable.
    /// </summary>
    private CompiledFilter? CompileFilter(
        StorageFilter? filter,
        CollectionDefinition scope,
        List<StorageValidationError> errors)
    {
        switch (filter)
        {
            case null:
                return null;

            case StorageGroupFilter group:
            {
                var children = group.Filters
                    .Select(child => CompileFilter(child, scope, errors))
                    .ToArray();
                return new CompiledGroup(group.IsOr, children!);
            }

            case StorageNotFilter not:
                return new CompiledNot(CompileFilter(not.Inner, scope, errors)!);

            case StorageRelationFilter relation:
            {
                if (!BelongsTo(relation.SourceColumn, scope))
                {
                    errors.Add(new StorageValidationError(
                        "ColumnNotInCollection",
                        relation.SourceColumn.Name,
                        $"Column '{relation.SourceColumn.Name}' does not belong to collection '{scope.Name}'."));
                    return null;
                }

                if (!BelongsTo(relation.TargetColumn, relation.TargetCollection))
                {
                    errors.Add(new StorageValidationError(
                        "ColumnNotInCollection",
                        relation.TargetColumn.Name,
                        $"Column '{relation.TargetColumn.Name}' does not belong to collection '{relation.TargetCollection.Name}'."));
                    return null;
                }

                // The nested condition is checked against the related collection.
                var inner = CompileFilter(relation.Inner, relation.TargetCollection, errors);
                return new CompiledRelation(
                    relation.TargetCollection.Name,
                    relation.SourceColumn.Name,
                    relation.TargetColumn.Name,
                    relation.Quantifier,
                    inner);
            }

            case StorageFieldFilter field:
                return CompileFieldFilter(field, scope, errors);

            default:
                errors.Add(new StorageValidationError("UnknownFilter", null, "Unsupported condition."));
                return null;
        }
    }

    private CompiledFilter? CompileFieldFilter(
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
            return null;
        }

        if (!IsOperatorAllowed(filter.Operator, column.Type))
        {
            errors.Add(new StorageValidationError(
                "OperatorNotAllowed",
                column.Name,
                $"Operator '{filter.Operator}' cannot be used on column '{column.Name}' of type {column.Type}."));
            return null;
        }

        var operands = new List<object?>(filter.Operands.Count);
        foreach (var operand in filter.Operands)
        {
            if (!TryConvertOperand(operand, column, out var converted))
            {
                errors.Add(new StorageValidationError(
                    "InvalidOperandType",
                    column.Name,
                    $"Value '{operand}' is not a valid {column.Type} for column '{column.Name}'."));
                return null;
            }

            operands.Add(converted);
        }

        return new CompiledField(column.Name, filter.Operator, operands);
    }

    private void ValidateSortAndSelect(StorageQuery query, List<StorageValidationError> errors)
    {
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

    /// <summary>
    /// Converts operand text to the column's type. Text operands are put through
    /// the column's semantic normalisation first, because stored values were
    /// normalised on write - without it a prefix search for "+380" would miss
    /// values stored as "380...".
    /// </summary>
    private bool TryConvertOperand(string? raw, ColumnDefinition column, out object? converted)
    {
        converted = null;
        if (raw is null)
        {
            return true;
        }

        var text = raw.Trim();

        if (column.Type == Core.ColumnType.String)
        {
            converted = NormalizeOperand(text, column);
            return true;
        }

        converted = column.Type switch
        {
            Core.ColumnType.Boolean => bool.TryParse(text, out var b) ? b : null,
            Core.ColumnType.Int32 => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
            Core.ColumnType.Int64 => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
            Core.ColumnType.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null,
            Core.ColumnType.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null,
            Core.ColumnType.Guid => Guid.TryParse(text, out var g) ? g : null,
            _ => text
        };

        return converted is not null;
    }

    private string NormalizeOperand(string text, ColumnDefinition column)
    {
        if (string.IsNullOrEmpty(column.SemanticTypeName) || _semanticTypeRegistry is null)
        {
            return text;
        }

        var semanticType = _semanticTypeRegistry.GetByNameOrAlias(column.SemanticTypeName);
        if (semanticType?.NormalizationRules is null || semanticType.NormalizationRules.Count == 0)
        {
            return text;
        }

        try
        {
            return StorageValidation.ApplyNormalizationRules(text, semanticType.NormalizationRules) as string ?? text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not normalise a query operand. Column: {ColumnName}, SemanticType: {SemanticTypeName}",
                column.Name,
                column.SemanticTypeName);
            return text;
        }
    }

    // =====================================================================
    // Executable form and evaluation
    // =====================================================================

    private abstract record CompiledFilter;

    private sealed record CompiledField(string Column, QueryOperator Operator, IReadOnlyList<object?> Operands)
        : CompiledFilter;

    private sealed record CompiledGroup(bool IsOr, IReadOnlyList<CompiledFilter> Filters) : CompiledFilter;

    private sealed record CompiledNot(CompiledFilter Inner) : CompiledFilter;

    private sealed record CompiledRelation(
        string TargetCollection,
        string SourceColumn,
        string TargetColumn,
        QueryQuantifier Quantifier,
        CompiledFilter? Inner) : CompiledFilter;

    private bool Matches(StorageRecord record, CompiledFilter? filter) =>
        filter switch
        {
            null => true,
            CompiledField field => MatchesField(record, field),
            CompiledNot not => !Matches(record, not.Inner),
            CompiledGroup group => group.IsOr
                ? group.Filters.Any(child => Matches(record, child))
                : group.Filters.All(child => Matches(record, child)),
            CompiledRelation relation => MatchesRelation(record, relation),
            _ => true
        };

    private bool MatchesRelation(StorageRecord record, CompiledRelation filter)
    {
        if (!record.Fields.TryGetValue(filter.SourceColumn, out var key) || key is null)
        {
            // Nothing to match on: only "none" can hold.
            return filter.Quantifier == QueryQuantifier.None;
        }

        var targetState = GetCollectionState(filter.TargetCollection);
        var related = targetState.Records.Values
            .Where(candidate =>
                candidate.Fields.TryGetValue(filter.TargetColumn, out var value) && ValuesEqual(value, key))
            .ToList();

        if (related.Count == 0)
        {
            // "all" over nothing is vacuously true, which is rarely the intent
            // behind a question phrased that way.
            return filter.Quantifier == QueryQuantifier.None;
        }

        var satisfying = related.Count(candidate => Matches(candidate, filter.Inner));

        return filter.Quantifier switch
        {
            QueryQuantifier.Any => satisfying > 0,
            QueryQuantifier.None => satisfying == 0,
            QueryQuantifier.All => satisfying == related.Count,
            _ => false
        };
    }

    private static bool MatchesField(StorageRecord record, CompiledField filter)
    {
        record.Fields.TryGetValue(filter.Column, out var value);

        switch (filter.Operator)
        {
            case QueryOperator.IsNull:
                return value is null;
            case QueryOperator.IsNotNull:
                return value is not null;
        }

        if (value is null)
        {
            return false;
        }

        return filter.Operator switch
        {
            QueryOperator.Equals => ValuesEqual(value, filter.Operands[0]),
            QueryOperator.NotEquals => !ValuesEqual(value, filter.Operands[0]),
            QueryOperator.In => filter.Operands.Any(operand => ValuesEqual(value, operand)),
            QueryOperator.StartsWith => AsText(value).StartsWith(AsText(filter.Operands[0]), StringComparison.OrdinalIgnoreCase),
            QueryOperator.EndsWith => AsText(value).EndsWith(AsText(filter.Operands[0]), StringComparison.OrdinalIgnoreCase),
            QueryOperator.Contains => AsText(value).Contains(AsText(filter.Operands[0]), StringComparison.OrdinalIgnoreCase),
            QueryOperator.GreaterThan => CompareValues(value, filter.Operands[0]) > 0,
            QueryOperator.GreaterOrEqual => CompareValues(value, filter.Operands[0]) >= 0,
            QueryOperator.LessThan => CompareValues(value, filter.Operands[0]) < 0,
            QueryOperator.LessOrEqual => CompareValues(value, filter.Operands[0]) <= 0,
            QueryOperator.Between => CompareValues(value, filter.Operands[0]) >= 0 &&
                                     CompareValues(value, filter.Operands[1]) <= 0,
            _ => false
        };
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is string || right is string)
        {
            return string.Equals(AsText(left), AsText(right), StringComparison.OrdinalIgnoreCase);
        }

        return CompareValues(left, right) == 0;
    }

    /// <summary>
    /// Orders two values, widening numbers so an Int32 column compares correctly
    /// against a decimal operand.
    /// </summary>
    private static int CompareValues(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null ? 0 : left is null ? -1 : 1;
        }

        if (TryAsDecimal(left, out var leftNumber) && TryAsDecimal(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is DateTime leftDate && right is DateTime rightDate)
        {
            return leftDate.CompareTo(rightDate);
        }

        if (left is IComparable comparable && left.GetType() == right.GetType())
        {
            return comparable.CompareTo(right);
        }

        return string.Compare(AsText(left), AsText(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAsDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal d: result = d; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case double db: result = (decimal)db; return true;
            case float f: result = (decimal)f; return true;
            default: result = 0; return false;
        }
    }

    private static string AsText(object? value) =>
        value is null ? string.Empty : RecordValueFormatter.Format(value);

    private static IEnumerable<StorageRecord> ApplyOrdering(
        List<StorageRecord> records,
        IReadOnlyList<StorageSort> orderBy)
    {
        if (orderBy.Count == 0)
        {
            return records;
        }

        var comparer = Comparer<object?>.Create(CompareValues);
        IOrderedEnumerable<StorageRecord>? ordered = null;

        foreach (var sort in orderBy)
        {
            var columnName = sort.Column.Name;
            object? Key(StorageRecord record) =>
                record.Fields.TryGetValue(columnName, out var value) ? value : null;

            ordered = ordered is null
                ? sort.Descending ? records.OrderByDescending(Key, comparer) : records.OrderBy(Key, comparer)
                : sort.Descending ? ordered.ThenByDescending(Key, comparer) : ordered.ThenBy(Key, comparer);
        }

        return ordered ?? (IEnumerable<StorageRecord>)records;
    }

    private static StorageQueryRow Project(StorageRecord record, IReadOnlyList<ColumnDefinition> select)
    {
        var names = select.Count == 0
            ? record.Fields.Keys.ToArray()
            : select.Select(column => column.Name).ToArray();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (record.Fields.TryGetValue(name, out var value))
            {
                fields[name] = value;
            }
        }

        return new StorageQueryRow(record.Id, fields);
    }
}
