namespace TokkDb.Assistant.Storage;

/// <summary>
/// What a condition asks of a column. Twelve, and deliberately not more.
///
/// D-4's premise is that a small model choosing between a few things is right more often than
/// one choosing between many, and a retrieval operation is where that bites: the model writes
/// the query (D-6) and nothing downstream can tell a wrong operator from a right one. So the
/// set is the smallest that covers what people actually ask of a personal storage, and every
/// member of it is a shape the engine's planner can answer from an index.
///
/// There is no <c>Between</c>: it is two conditions, and having both spellings would mean the
/// model choosing between them. There is no arithmetic and nothing that names a second column.
/// </summary>
public enum QueryOperator
{
    /// <summary>The column holds this value. Any type.</summary>
    Equals = 1,

    /// <summary>The column holds something else. Any type.</summary>
    NotEquals,

    /// <summary>Any type but <see cref="ColumnType.Boolean"/>, which has no order worth asking about.</summary>
    LessThan,

    /// <summary>Any type but <see cref="ColumnType.Boolean"/>.</summary>
    LessOrEqual,

    /// <summary>Any type but <see cref="ColumnType.Boolean"/>.</summary>
    GreaterThan,

    /// <summary>Any type but <see cref="ColumnType.Boolean"/>.</summary>
    GreaterOrEqual,

    /// <summary><see cref="ColumnType.Text"/> only.</summary>
    StartsWith,

    /// <summary><see cref="ColumnType.Text"/> only.</summary>
    EndsWith,

    /// <summary><see cref="ColumnType.Text"/> only.</summary>
    Contains,

    /// <summary>
    /// The column holds one of these values. One condition rather than a group of alternatives,
    /// because a set of equalities is a shape an index can answer and an OR is not.
    /// </summary>
    In,

    /// <summary>The column has no value: it was never given one, or it was given nothing. No operands.</summary>
    IsNothing,

    /// <summary>The column has a value. No operands.</summary>
    IsSomething
}

/// <summary>
/// One thing asked of one column.
///
/// The column is named rather than passed as a <see cref="ColumnDefinition"/>. The old contract
/// took resolved definitions and needed a binder in front of it to produce them, which meant a
/// query could not be written down, sent, stored in a trace, or composed by anything that did
/// not already hold the schema. A name and a value can be all three, and resolving them against
/// the schema is what <see cref="IStorage.ExecuteQuery"/> does before it runs anything.
///
/// The values are values, not text. A condition on a decimal column carries a
/// <see cref="decimal"/>, and text offered where a decimal belongs is refused with the same
/// <see cref="ColumnTypeMismatch"/> a write would give - one rule for what a value of a column
/// is, whether it is being stored or being looked for.
/// </summary>
public sealed record QueryCondition
{
    public QueryCondition(string columnName, QueryOperator @operator, params object?[] values)
        : this(columnName, @operator, (IReadOnlyList<object?>)values)
    {
    }

    public QueryCondition(string columnName, QueryOperator @operator, IReadOnlyList<object?> values)
    {
        ColumnName = StorageNames.Normalise(columnName, "column name");

        if (!Enum.IsDefined(@operator))
        {
            throw new InvalidDefinitionException("condition", $"'{columnName}' was asked something with no operator.");
        }

        Operator = @operator;
        Values = [.. values ?? []];
    }

    public string ColumnName { get; }

    public QueryOperator Operator { get; }

    /// <summary>
    /// What the operator is asked against: one value for most, one or more for
    /// <see cref="QueryOperator.In"/>, none for <see cref="QueryOperator.IsNothing"/> and
    /// <see cref="QueryOperator.IsSomething"/>. A wrong number is refused by validation, naming
    /// the column, rather than being guessed at.
    /// </summary>
    public IReadOnlyList<object?> Values { get; }

    public override string ToString() =>
        Values.Count == 0 ? $"{ColumnName} {Operator}" : $"{ColumnName} {Operator} {string.Join(", ", Values)}";
}

/// <summary>One column to sort by, and which way.</summary>
public sealed record QuerySort(string ColumnName, bool Descending = false)
{
    public string ColumnName { get; } = StorageNames.Normalise(ColumnName, "column name");
}

/// <summary>What an aggregation asks of the records a query matched.</summary>
public enum AggregateFunction
{
    /// <summary>How many records matched. The only one that names no column.</summary>
    Count = 1,

    /// <summary>The values added up. Numbers only.</summary>
    Sum,

    /// <summary>The smallest value. Any ordered type.</summary>
    Minimum,

    /// <summary>The largest value. Any ordered type.</summary>
    Maximum,

    /// <summary>The mean of the values. Numbers only, and exact rather than binary floating point.</summary>
    Average
}

/// <summary>
/// One number computed over everything a query matched, rather than over the page it returned.
///
/// D-16: aggregation is computed by the engine and never by a model. "What did I spend on
/// conferences last year" is a sum over four hundred records, and the alternative to computing it
/// here is sending four hundred records to a model and hoping - which costs the tokens D-6 exists
/// to save and gets the arithmetic wrong.
/// </summary>
public sealed record QueryAggregate
{
    public QueryAggregate(AggregateFunction function, string? columnName = null)
    {
        if (!Enum.IsDefined(function))
        {
            throw new InvalidDefinitionException("aggregate", "A query was asked for a total of no kind.");
        }

        if (function is AggregateFunction.Count)
        {
            // Counting records needs no column, and one given here would be a caller believing
            // it counts values rather than records.
            if (columnName is not null)
            {
                throw new InvalidDefinitionException(
                    "aggregate",
                    $"Counting records takes no column, and '{columnName}' was named.");
            }

            ColumnName = null;
        }
        else
        {
            ColumnName = StorageNames.Normalise(
                columnName ?? throw new InvalidDefinitionException(
                    "aggregate", $"{function} has to be asked of a column."),
                "column name");
        }

        Function = function;
    }

    public AggregateFunction Function { get; }

    /// <summary>The column, or null for <see cref="AggregateFunction.Count"/>.</summary>
    public string? ColumnName { get; }

    /// <summary>
    /// What this aggregate is called in <see cref="StorageQueryResult.Aggregates"/>: "count",
    /// "sum(cost)". Stable, because a caller reads the result by it.
    /// </summary>
    public string Key => ColumnName is null
        ? Function.ToString().ToLowerInvariant()
        : $"{Function.ToString().ToLowerInvariant()}({ColumnName})";

    public override string ToString() => Key;
}

/// <summary>
/// A condition on the far side of a relation: the expenses whose conference was in Lviv, said
/// without the caller having to know that "conference" is a column holding a name.
///
/// SC-7's internal model supports relation traversal, and this is the shape of it. The relation
/// says which two collections and which two columns; the query says what has to be true of the
/// far one. The storage runs the far query first and turns its answers into a condition on the
/// near column, which is a shape an index answers - so traversal costs one extra query and not a
/// join the engine has no operator for.
/// </summary>
public sealed record QueryTraversal
{
    public QueryTraversal(string relationName, StorageQuery target)
    {
        RelationName = StorageNames.Normalise(relationName, "relation name");
        Target = target ?? throw new InvalidDefinitionException(
            "traversal", $"Following '{RelationName}' needs something to look for on the other side.");
    }

    public string RelationName { get; }

    /// <summary>What has to be true of the record referred to.</summary>
    public StorageQuery Target { get; }

    public override string ToString() => $"through {RelationName} to {Target.CollectionName}";
}

/// <summary>
/// A read, described rather than performed. The rich face of D-16.
///
/// SC-7: one declarative type, validated against the schema before anything runs. It is
/// declarative in the strict sense - it says what is wanted and nothing about how to get it.
/// Which index to use, whether to use one at all, and in what order to read the pages are the
/// planner's, and what it decided comes back in <see cref="StorageQueryResult.Execution"/>
/// rather than being something the caller had to ask for.
///
/// <b>This is the face C# writes, and it is not the face a model writes.</b> D-16 keeps the two
/// apart deliberately. Everything here - projection, cursor paging, aggregation, traversal -
/// serves the browser, retrieval, follow-ups and the import path, and every one of them would be
/// another thing a 4B model has to get right if it had to emit them. What a model emits is
/// <see cref="ModelQuery"/>, which is small enough for a grammar to constrain, and C# expands it
/// into this. Adding a feature here therefore changes nothing about what a model has to learn.
///
/// <b>The conditions are joined by AND, and there is no OR.</b> That is a limit and it is
/// deliberate. Every condition of an AND is a shape the planner can answer from an index and
/// re-check cheaply; an OR is not, and it arrives at the engine as a residual that is evaluated
/// against every record the access path returns - which is the full scan DC-5 exists to make
/// visible. The case people actually ask for, several values of one column, is
/// <see cref="QueryOperator.In"/>, which an index answers. If a query across two columns with
/// an OR is ever needed, it is two queries and a union, and the cost of that is visible where
/// a residual's is not.
/// </summary>
public sealed record StorageQuery
{
    public StorageQuery(
        string collectionName,
        IReadOnlyList<QueryCondition>? where = null,
        IReadOnlyList<QuerySort>? orderBy = null,
        int skip = 0,
        int? take = null,
        IReadOnlyList<Ulid>? ids = null)
        : this(collectionName, where, orderBy, skip, take, ids, null, null, null, null)
    {
    }

    private StorageQuery(
        string collectionName,
        IReadOnlyList<QueryCondition>? where,
        IReadOnlyList<QuerySort>? orderBy,
        int skip,
        int? take,
        IReadOnlyList<Ulid>? ids,
        IReadOnlyList<string>? select,
        QueryCursor? after,
        IReadOnlyList<QueryAggregate>? aggregates,
        IReadOnlyList<QueryTraversal>? traversals)
    {
        CollectionName = StorageNames.Normalise(collectionName, "collection name");
        Where = [.. where ?? []];
        OrderBy = [.. orderBy ?? []];

        if (skip < 0)
        {
            throw new InvalidDefinitionException("skip", $"A query cannot skip {skip} records.");
        }

        if (take is < 0)
        {
            throw new InvalidDefinitionException("take", $"A query cannot take {take} records.");
        }

        Skip = skip;
        Take = take;
        Ids = [.. ids ?? []];
        Select = [.. (select ?? []).Select(static column => StorageNames.Normalise(column, "column name"))];
        After = after;
        Aggregates = [.. aggregates ?? []];
        Traversals = [.. traversals ?? []];
    }

    public string CollectionName { get; }

    /// <summary>Everything that has to be true of a record, all of it at once.</summary>
    public IReadOnlyList<QueryCondition> Where { get; }

    /// <summary>
    /// How the records come back. An empty list means no order is asked for and none is
    /// promised, exactly as <see cref="IStorage.GetAll"/> promises none - except when a cursor
    /// is in play, where identity is the order and is what makes a page boundary exact.
    /// </summary>
    public IReadOnlyList<QuerySort> OrderBy { get; }

    /// <summary>
    /// How many records to pass over. Kept for callers that have a reason, and <b>not</b> what
    /// the browser pages with: BR-3 pages by cursor, because an offset re-counts from the start
    /// every time and is wrong the moment something is inserted before it.
    /// </summary>
    public int Skip { get; }

    /// <summary>How many records at most, or null for all of them.</summary>
    public int? Take { get; }

    /// <summary>
    /// Records by identity, if the caller has them. Identity is not a column (SC-4), so it
    /// cannot be a condition; an empty list asks nothing about identity. Given, it is the
    /// narrowest access path there is, and the planner takes it.
    /// </summary>
    public IReadOnlyList<Ulid> Ids { get; }

    /// <summary>
    /// The columns to bring back, or empty for all of them. A projection over a wide collection
    /// is the difference between reading a name and reading a record, and the browser's table
    /// asks for four columns of forty.
    /// </summary>
    public IReadOnlyList<string> Select { get; }

    /// <summary>Where the previous page ended, or null to start at the beginning. See <see cref="QueryCursor"/>.</summary>
    public QueryCursor? After { get; }

    /// <summary>What to compute over everything that matched, rather than over the page returned.</summary>
    public IReadOnlyList<QueryAggregate> Aggregates { get; }

    /// <summary>Conditions on the far side of a relation. See <see cref="QueryTraversal"/>.</summary>
    public IReadOnlyList<QueryTraversal> Traversals { get; }

    /// <summary>This query, bringing back only these columns.</summary>
    public StorageQuery Selecting(params string[] columns) =>
        new(CollectionName, Where, OrderBy, Skip, Take, Ids, columns, After, Aggregates, Traversals);

    /// <summary>This query, continuing after the page that cursor ended.</summary>
    public StorageQuery Continuing(QueryCursor? cursor) =>
        new(CollectionName, Where, OrderBy, Skip, Take, Ids, Select, cursor, Aggregates, Traversals);

    /// <summary>This query, also computing these.</summary>
    public StorageQuery Computing(params QueryAggregate[] aggregates) =>
        new(CollectionName, Where, OrderBy, Skip, Take, Ids, Select, After, [.. Aggregates, .. aggregates], Traversals);

    /// <summary>This query, with a condition on the far side of a relation.</summary>
    public StorageQuery Through(QueryTraversal traversal) =>
        new(CollectionName, Where, OrderBy, Skip, Take, Ids, Select, After, Aggregates, [.. Traversals, traversal]);

    /// <summary>
    /// This query with one more condition and no traversals: what a resolved traversal becomes,
    /// once the far side has been asked and its answers are a condition on the near column.
    /// </summary>
    public StorageQuery Resolved(IReadOnlyList<QueryCondition> extra) =>
        new(CollectionName, [.. Where, .. extra], OrderBy, Skip, Take, Ids, Select, After, Aggregates, []);

    /// <summary>This query, taking at most that many records.</summary>
    public StorageQuery Taking(int? take) =>
        new(CollectionName, Where, OrderBy, Skip, take, Ids, Select, After, Aggregates, Traversals);
}
