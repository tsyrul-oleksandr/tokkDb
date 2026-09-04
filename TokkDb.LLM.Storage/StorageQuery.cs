namespace TokkDb.LLM.Storage;

public enum QueryOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    StartsWith,
    EndsWith,
    Contains,
    In,
    Between,
    IsNull,
    IsNotNull
}

public enum QueryQuantifier
{
    Any,
    None,
    All
}

/// <summary>
/// Storage-side query condition.
///
/// Every reference here is a resolved definition rather than a name: the binder
/// has already established that the column and relation exist. What remains for
/// storage is whether the definitions fit together - the column belonging to the
/// collection under filter, the operator suiting the column type, and the
/// operands converting to it.
/// </summary>
public abstract record StorageFilter;

/// <summary>
/// Comparison against a column. <see cref="Operands"/> is still the text the
/// caller supplied; storage converts it using <see cref="ColumnDefinition.Type"/>
/// so that a bad value is reported as a validation error rather than guessed at.
/// </summary>
public sealed record StorageFieldFilter(
    ColumnDefinition Column,
    QueryOperator Operator,
    IReadOnlyList<string?> Operands) : StorageFilter;

public sealed record StorageGroupFilter(
    bool IsOr,
    IReadOnlyList<StorageFilter> Filters) : StorageFilter;

public sealed record StorageNotFilter(StorageFilter Inner) : StorageFilter;

/// <summary>
/// A step across a declared relation. <see cref="SourceColumn"/> belongs to the
/// collection currently under filter and <see cref="TargetColumn"/> to
/// <see cref="TargetCollection"/>, whichever way round the relation was
/// declared.
/// </summary>
public sealed record StorageRelationFilter(
    RelationDefinition Relation,
    ColumnDefinition SourceColumn,
    CollectionDefinition TargetCollection,
    ColumnDefinition TargetColumn,
    QueryQuantifier Quantifier,
    StorageFilter? Inner) : StorageFilter;

public sealed record StorageSort(ColumnDefinition Column, bool Descending);

/// <summary>
/// A query expressed entirely in schema definitions, ready for storage to
/// validate and run.
///
/// <see cref="Ids"/> is the one reference that is not a schema definition: a
/// record's identity is not a column, so a lookup by id cannot be phrased as a
/// filter. An empty or null list places no restriction on identity.
/// </summary>
public sealed record StorageQuery(
    CollectionDefinition Collection,
    StorageFilter? Where,
    IReadOnlyList<StorageSort> OrderBy,
    int Skip,
    int Take,
    IReadOnlyList<ColumnDefinition> Select,
    IReadOnlyList<Guid>? Ids = null);

public sealed record StorageQueryRow(
    Guid Id,
    IReadOnlyDictionary<string, object?> Fields);

public sealed record StorageQueryResult(
    string CollectionName,
    IReadOnlyList<StorageQueryRow> Rows,
    int Skip,
    int Take);
