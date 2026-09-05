using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage;

public interface IRecordQueryBinder
{
    /// <summary>
    /// Converts a tool query into a storage query, replacing every name with the
    /// definition it refers to.
    /// </summary>
    /// <exception cref="StorageValidationException">
    /// Thrown when a name cannot be resolved, or the request is malformed.
    /// </exception>
    StorageQuery Bind(IStorage storage, RecordQuery query);
}

/// <summary>
/// Turns names into definitions.
///
/// This is the only stage that deals in strings. A column, collection or
/// relation that does not exist is reported here as "not found" - the resulting
/// <see cref="StorageQuery"/> can only ever reference definitions that came from
/// the schema, so storage never receives an unresolved name.
///
/// Whether the resolved pieces fit together - operand types, column ownership -
/// is storage's decision, not this one's.
/// </summary>
public sealed class RecordQueryBinder : IRecordQueryBinder
{
    public const int MaxFilterDepth = 4;
    public const int MaxFilterNodes = 48;
    public const int MaxTake = 500;

    /// <summary>
    /// Take applied when the caller does not ask for one. Deliberately small:
    /// an unfiltered query is a legitimate way to look at a collection, and the
    /// default is what stops that from returning every record it holds.
    /// </summary>
    public const int DefaultTake = 10;

    public StorageQuery Bind(IStorage storage, RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(query);

        var errors = new List<StorageValidationError>();

        var collectionName = query.CollectionName?.Trim() ?? string.Empty;
        if (collectionName.Length == 0)
        {
            throw new StorageValidationException(
            [
                new StorageValidationError("CollectionNameRequired", null, "collectionName is required.")
            ]);
        }

        var collection = storage.GetCollectionDefinition(collectionName);
        if (collection is null)
        {
            throw new StorageValidationException(
            [
                new StorageValidationError(
                    "CollectionNotFound",
                    null,
                    $"Collection '{collectionName}' not found. Available collections: " +
                    string.Join(", ", storage.GetCollectionDefinitions().Select(c => c.Name)) + ".")
            ]);
        }

        var ids = BindIds(query.RecordIds, errors);

        var relations = storage.GetRelations();
        var nodeBudget = MaxFilterNodes;

        var where = query.Where is null
            ? null
            : BindFilter(query.Where, collection, storage, relations, errors, depth: 1, ref nodeBudget);

        var orderBy = BindSort(query.OrderBy, collection, errors);
        var select = BindSelect(query.Select, collection, errors);

        var skip = query.Skip ?? 0;
        if (skip < 0)
        {
            errors.Add(new StorageValidationError("InvalidSkip", null, "skip must be zero or greater."));
        }

        var take = query.Take ?? DefaultTake;
        if (take < 1 || take > MaxTake)
        {
            errors.Add(new StorageValidationError(
                "InvalidTake", null, $"take must be between 1 and {MaxTake}."));
        }

        if (errors.Count > 0)
        {
            throw new StorageValidationException(errors);
        }

        return new StorageQuery(collection, where, orderBy, skip, take, select, ids);
    }

    /// <summary>
    /// Resolves the requested record ids. A record's identity is not a column,
    /// so this is checked here rather than through the filter tree: an id that
    /// is not a ULID could never match anything, and saying so is more useful
    /// than returning nothing.
    /// </summary>
    private static IReadOnlyList<Ulid>? BindIds(
        List<string>? recordIds,
        List<StorageValidationError> errors)
    {
        if (recordIds is null || recordIds.Count == 0)
        {
            return null;
        }

        var ids = new List<Ulid>(recordIds.Count);
        foreach (var raw in recordIds)
        {
            if (Ulid.TryParse(raw?.Trim(), out var id))
            {
                ids.Add(id);
                continue;
            }

            errors.Add(new StorageValidationError(
                "InvalidRecordId",
                null,
                $"Record id '{raw}' is not a valid id. Ids come from a previous query result."));
        }

        return ids;
    }

    // =====================================================================
    // Filter tree
    // =====================================================================

    private StorageFilter? BindFilter(
        RecordFilter filter,
        CollectionDefinition scope,
        IStorage storage,
        IReadOnlyCollection<RelationDefinition> relations,
        List<StorageValidationError> errors,
        int depth,
        ref int nodeBudget)
    {
        if (depth > MaxFilterDepth)
        {
            errors.Add(new StorageValidationError(
                "FilterTooDeep", null, $"Filter is nested deeper than {MaxFilterDepth} levels."));
            return null;
        }

        if (--nodeBudget < 0)
        {
            errors.Add(new StorageValidationError(
                "FilterTooLarge", null, $"Filter contains more than {MaxFilterNodes} conditions."));
            return null;
        }

        var shapes = 0;
        if (!string.IsNullOrWhiteSpace(filter.Field)) shapes++;
        if (!string.IsNullOrWhiteSpace(filter.Logic)) shapes++;
        if (!string.IsNullOrWhiteSpace(filter.Relation)) shapes++;

        if (shapes != 1)
        {
            errors.Add(new StorageValidationError(
                "InvalidFilterShape",
                null,
                shapes == 0
                    ? "Each condition must set exactly one of field, logic or relation."
                    : "A condition must set only one of field, logic or relation."));
            return null;
        }

        if (!string.IsNullOrWhiteSpace(filter.Field))
        {
            return BindFieldFilter(filter, scope, errors);
        }

        return !string.IsNullOrWhiteSpace(filter.Logic)
            ? BindGroupFilter(filter, scope, storage, relations, errors, depth, ref nodeBudget)
            : BindRelationFilter(filter, scope, storage, relations, errors, depth, ref nodeBudget);
    }

    private StorageFilter? BindGroupFilter(
        RecordFilter filter,
        CollectionDefinition scope,
        IStorage storage,
        IReadOnlyCollection<RelationDefinition> relations,
        List<StorageValidationError> errors,
        int depth,
        ref int nodeBudget)
    {
        var logic = filter.Logic!.Trim().ToLowerInvariant();
        if (logic is not ("and" or "or" or "not"))
        {
            errors.Add(new StorageValidationError(
                "UnknownLogic", null, $"Unknown logic '{filter.Logic}'. Use and, or or not."));
            return null;
        }

        var children = filter.Filters ?? [];
        if (children.Count == 0)
        {
            errors.Add(new StorageValidationError(
                "EmptyLogic", null, $"Logic '{logic}' requires at least one nested condition."));
            return null;
        }

        if (logic == "not" && children.Count != 1)
        {
            errors.Add(new StorageValidationError(
                "InvalidNot", null, "Logic 'not' requires exactly one nested condition."));
            return null;
        }

        var bound = new List<StorageFilter>(children.Count);
        foreach (var child in children)
        {
            var result = BindFilter(child, scope, storage, relations, errors, depth + 1, ref nodeBudget);
            if (result is not null)
            {
                bound.Add(result);
            }
        }

        if (bound.Count != children.Count)
        {
            return null;
        }

        return logic == "not"
            ? new StorageNotFilter(bound[0])
            : new StorageGroupFilter(logic == "or", bound);
    }

    private StorageFilter? BindRelationFilter(
        RecordFilter filter,
        CollectionDefinition scope,
        IStorage storage,
        IReadOnlyCollection<RelationDefinition> relations,
        List<StorageValidationError> errors,
        int depth,
        ref int nodeBudget)
    {
        var relationName = filter.Relation!.Trim();
        var relation = relations.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, relationName, StringComparison.OrdinalIgnoreCase));

        if (relation is null)
        {
            errors.Add(new StorageValidationError(
                "RelationNotFound",
                null,
                $"Relation '{relationName}' not found. Available relations: " +
                (relations.Count == 0 ? "none" : string.Join(", ", relations.Select(r => r.Name))) + "."));
            return null;
        }

        // The relation may be declared from either end; the collection under
        // filter decides which side is the source of this step.
        string sourceColumnName, targetCollectionName, targetColumnName;
        if (string.Equals(relation.SourceCollection, scope.Name, StringComparison.OrdinalIgnoreCase))
        {
            sourceColumnName = relation.SourceColumn;
            targetCollectionName = relation.TargetCollection;
            targetColumnName = relation.TargetColumn;
        }
        else if (string.Equals(relation.TargetCollection, scope.Name, StringComparison.OrdinalIgnoreCase))
        {
            sourceColumnName = relation.TargetColumn;
            targetCollectionName = relation.SourceCollection;
            targetColumnName = relation.SourceColumn;
        }
        else
        {
            errors.Add(new StorageValidationError(
                "RelationNotApplicable",
                null,
                $"Relation '{relation.Name}' connects {relation.SourceCollection} and {relation.TargetCollection}, " +
                $"so it cannot be followed from '{scope.Name}'."));
            return null;
        }

        var targetCollection = storage.GetCollectionDefinition(targetCollectionName);
        if (targetCollection is null)
        {
            errors.Add(new StorageValidationError(
                "CollectionNotFound",
                null,
                $"Collection '{targetCollectionName}' used by relation '{relation.Name}' not found."));
            return null;
        }

        var sourceColumn = FindColumn(scope, sourceColumnName, errors);
        var targetColumn = FindColumn(targetCollection, targetColumnName, errors);
        if (sourceColumn is null || targetColumn is null)
        {
            return null;
        }

        var quantifierText = string.IsNullOrWhiteSpace(filter.Quantifier)
            ? "any"
            : filter.Quantifier.Trim().ToLowerInvariant();

        QueryQuantifier quantifier;
        switch (quantifierText)
        {
            case "any": quantifier = QueryQuantifier.Any; break;
            case "none": quantifier = QueryQuantifier.None; break;
            case "all": quantifier = QueryQuantifier.All; break;
            default:
                errors.Add(new StorageValidationError(
                    "UnknownQuantifier",
                    null,
                    $"Unknown quantifier '{filter.Quantifier}'. Use any, none or all."));
                return null;
        }

        // The nested condition is bound against the related collection, so its
        // columns are resolved in the scope they actually belong to.
        StorageFilter? inner = null;
        if (filter.Where is not null)
        {
            inner = BindFilter(filter.Where, targetCollection, storage, relations, errors, depth + 1, ref nodeBudget);
            if (inner is null)
            {
                return null;
            }
        }

        return new StorageRelationFilter(
            relation, sourceColumn, targetCollection, targetColumn, quantifier, inner);
    }

    private static StorageFilter? BindFieldFilter(
        RecordFilter filter,
        CollectionDefinition scope,
        List<StorageValidationError> errors)
    {
        var column = FindColumn(scope, filter.Field!.Trim(), errors);
        if (column is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(filter.Operator))
        {
            errors.Add(new StorageValidationError(
                "OperatorRequired", column.Name, $"Condition on '{column.Name}' is missing an operator."));
            return null;
        }

        var op = ParseOperator(filter.Operator);
        if (op is null)
        {
            errors.Add(new StorageValidationError(
                "UnknownOperator",
                column.Name,
                $"Unknown operator '{filter.Operator}' on column '{column.Name}'."));
            return null;
        }

        var operands = CollectOperands(filter, op.Value, column, errors);
        return operands is null ? null : new StorageFieldFilter(column, op.Value, operands);
    }

    /// <summary>
    /// Gathers the operand text for an operator, checking only how many were
    /// supplied. Their type is storage's concern.
    /// </summary>
    private static IReadOnlyList<string?>? CollectOperands(
        RecordFilter filter,
        QueryOperator op,
        ColumnDefinition column,
        List<StorageValidationError> errors)
    {
        switch (op)
        {
            case QueryOperator.IsNull:
            case QueryOperator.IsNotNull:
                return Array.Empty<string?>();

            case QueryOperator.In:
            {
                var values = filter.Values ?? (filter.Value is null ? [] : [filter.Value]);
                if (values.Count == 0)
                {
                    errors.Add(new StorageValidationError(
                        "OperandRequired", column.Name,
                        $"Operator 'in' on '{column.Name}' requires at least one value."));
                    return null;
                }

                return values.Cast<string?>().ToArray();
            }

            case QueryOperator.Between:
            {
                var values = filter.Values ?? [];
                if (values.Count != 2)
                {
                    errors.Add(new StorageValidationError(
                        "OperandRequired", column.Name,
                        $"Operator 'between' on '{column.Name}' requires exactly two values."));
                    return null;
                }

                return values.Cast<string?>().ToArray();
            }

            default:
            {
                if (filter.Value is null)
                {
                    errors.Add(new StorageValidationError(
                        "OperandRequired", column.Name,
                        $"Condition on '{column.Name}' is missing a value."));
                    return null;
                }

                return new string?[] { filter.Value };
            }
        }
    }

    // =====================================================================
    // Sorting and projection
    // =====================================================================

    private static IReadOnlyList<StorageSort> BindSort(
        List<RecordQuerySort>? sorts,
        CollectionDefinition collection,
        List<StorageValidationError> errors)
    {
        if (sorts is null || sorts.Count == 0)
        {
            return Array.Empty<StorageSort>();
        }

        var bound = new List<StorageSort>(sorts.Count);
        foreach (var sort in sorts)
        {
            var column = FindColumn(collection, sort.Column?.Trim() ?? string.Empty, errors);
            if (column is null)
            {
                continue;
            }

            var direction = sort.Direction?.Trim().ToLowerInvariant();
            if (direction is not (null or "" or "asc" or "desc"))
            {
                errors.Add(new StorageValidationError(
                    "UnknownSortDirection",
                    column.Name,
                    $"Unknown sort direction '{sort.Direction}'. Use asc or desc."));
                continue;
            }

            bound.Add(new StorageSort(column, direction == "desc"));
        }

        return bound;
    }

    private static IReadOnlyList<ColumnDefinition> BindSelect(
        List<string>? select,
        CollectionDefinition collection,
        List<StorageValidationError> errors)
    {
        if (select is null || select.Count == 0)
        {
            return Array.Empty<ColumnDefinition>();
        }

        var bound = new List<ColumnDefinition>(select.Count);
        foreach (var name in select)
        {
            var column = FindColumn(collection, name?.Trim() ?? string.Empty, errors);
            if (column is not null)
            {
                bound.Add(column);
            }
        }

        return bound;
    }

    /// <summary>
    /// Resolves a column name within a collection. A name that does not resolve
    /// leaves no definition to bind, which is the "Column not found" error.
    /// </summary>
    private static ColumnDefinition? FindColumn(
        CollectionDefinition collection,
        string name,
        List<StorageValidationError> errors)
    {
        var column = collection.Columns.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (column is null)
        {
            errors.Add(new StorageValidationError(
                "ColumnNotFound",
                name,
                $"Column '{name}' not found in collection '{collection.Name}'. " +
                $"Available columns: {string.Join(", ", collection.Columns.Select(c => c.Name))}."));
        }

        return column;
    }

    private static QueryOperator? ParseOperator(string op) =>
        op.Trim().ToLowerInvariant() switch
        {
            "eq" or "equals" or "=" or "==" => QueryOperator.Equals,
            "neq" or "ne" or "!=" => QueryOperator.NotEquals,
            "gt" or ">" => QueryOperator.GreaterThan,
            "gte" or ">=" => QueryOperator.GreaterOrEqual,
            "lt" or "<" => QueryOperator.LessThan,
            "lte" or "<=" => QueryOperator.LessOrEqual,
            "startswith" => QueryOperator.StartsWith,
            "endswith" => QueryOperator.EndsWith,
            "contains" => QueryOperator.Contains,
            "in" => QueryOperator.In,
            "between" => QueryOperator.Between,
            "isnull" => QueryOperator.IsNull,
            "isnotnull" => QueryOperator.IsNotNull,
            _ => null
        };
}
