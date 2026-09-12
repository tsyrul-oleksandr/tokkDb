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

/// <summary>
/// A read, described rather than performed.
///
/// SC-7: one declarative type, validated against the schema before anything runs. It is
/// declarative in the strict sense - it says what is wanted and nothing about how to get it.
/// Which index to use, whether to use one at all, and in what order to read the pages are the
/// planner's, and what it decided comes back in <see cref="StorageQueryResult.AccessPath"/>
/// rather than being something the caller had to ask for.
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
    }

    public string CollectionName { get; }

    /// <summary>Everything that has to be true of a record, all of it at once.</summary>
    public IReadOnlyList<QueryCondition> Where { get; }

    /// <summary>
    /// How the records come back. An empty list means no order is asked for and none is
    /// promised, exactly as <see cref="IStorage.GetAll"/> promises none.
    /// </summary>
    public IReadOnlyList<QuerySort> OrderBy { get; }

    public int Skip { get; }

    /// <summary>How many records at most, or null for all of them.</summary>
    public int? Take { get; }

    /// <summary>
    /// Records by identity, if the caller has them. Identity is not a column (SC-4), so it
    /// cannot be a condition; an empty list asks nothing about identity. Given, it is the
    /// narrowest access path there is, and the planner takes it.
    /// </summary>
    public IReadOnlyList<Ulid> Ids { get; }
}
