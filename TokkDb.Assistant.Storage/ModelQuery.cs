namespace TokkDb.Assistant.Storage;

/// <summary>
/// What a condition can ask when a model is the one writing it.
///
/// Ten, against the internal model's twelve, and the difference is not arbitrary. Every one of
/// these is a thing a person says out loud - is, is not, before, after, at least, at most,
/// contains, one of, has nothing, has something - so the mapping from a sentence to an operator
/// is short. What
/// is missing is what a model gets wrong: <c>StartsWith</c> and <c>EndsWith</c> next to
/// <c>Contains</c> are three ways to say one thing and a 4B model picks among them badly, so the
/// narrow face keeps <c>Contains</c> and C# is free to expand a prefix search where it knows one
/// is meant.
/// </summary>
public enum ModelOperator
{
    Is = 1,
    IsNot,
    Before,
    After,
    AtLeast,
    AtMost,
    Contains,
    OneOf,
    HasNothing,
    HasSomething
}

/// <summary>One thing a model asked of one column.</summary>
public sealed record ModelCondition
{
    public ModelCondition(string column, ModelOperator @operator, params object?[] values)
        : this(column, @operator, (IReadOnlyList<object?>)values)
    {
    }

    public ModelCondition(string column, ModelOperator @operator, IReadOnlyList<object?> values)
    {
        Column = StorageNames.Normalise(column, "column name");

        if (!Enum.IsDefined(@operator))
        {
            throw new InvalidDefinitionException("condition", $"'{column}' was asked something with no operator.");
        }

        Operator = @operator;
        Values = [.. values ?? []];
    }

    public string Column { get; }

    public ModelOperator Operator { get; }

    public IReadOnlyList<object?> Values { get; }
}

/// <summary>
/// A read as a model is allowed to write it. The narrow face of D-16.
///
/// <b>Why there are two faces and not one.</b> The same abstraction serves retrieval, the
/// browser's table, sorting and filtering, follow-up questions and aggregation, so it has to be
/// rich. A rich query model that a model must emit is a small SQL that a 4B model has to get
/// right, and the spike gives no reason to expect that. So the model emits this - one collection,
/// a few conditions, one ordering, a limit - and C# expands it into <see cref="StorageQuery"/>,
/// which is where projection, cursors, aggregation and relation traversal live.
///
/// <b>What follows from the split, and it is the point of it.</b> Adding a feature to the
/// internal model requires no change to what a model emits, no change to the grammar that
/// constrains it, and no re-measurement of the accuracy R-3 is about. This type is therefore
/// deliberately boring, and a test pins its shape so that it stays that way: a field added here
/// is a field a model has to learn, which is a cost paid in accuracy rather than in code.
///
/// Everything in it is re-checked in C# against the schema before it runs. R-2 is why: the
/// grammar Ollama compiles enforces <c>type</c>, <c>enum</c>, <c>required</c> and
/// <c>additionalProperties</c> and nothing else, so a bound expressed any other way is a
/// suggestion.
/// </summary>
public sealed record ModelQuery
{
    public ModelQuery(
        string collection,
        IReadOnlyList<ModelCondition>? where = null,
        string? orderBy = null,
        bool newestFirst = false,
        int? limit = null)
    {
        Collection = StorageNames.Normalise(collection, "collection name");
        Where = [.. where ?? []];
        OrderBy = orderBy is null ? null : StorageNames.Normalise(orderBy, "column name");
        NewestFirst = newestFirst;

        if (limit is < 0)
        {
            throw new InvalidDefinitionException("limit", $"A query cannot ask for {limit} records.");
        }

        Limit = limit;
    }

    public string Collection { get; }

    public IReadOnlyList<ModelCondition> Where { get; }

    /// <summary>One column to order by, or null. One, because two is a thing to get wrong.</summary>
    public string? OrderBy { get; }

    /// <summary>Largest or latest first. "Descending" is not a word in this vocabulary.</summary>
    public bool NewestFirst { get; }

    public int? Limit { get; }
}

/// <summary>
/// The expansion from what a model emits to what the storage runs.
///
/// It is deliberately mechanical. Nothing here decides anything the model did not say: every
/// column, value and limit comes across unchanged, and what the narrow face has no word for -
/// projection, cursors, aggregation, traversal - is simply absent rather than guessed at. The
/// judgement about whether any of it fits the schema happens afterwards, in
/// <see cref="IStorage.ExecuteQuery"/>, so that a model's mistake is reported with the same
/// errors a caller's mistake would be.
/// </summary>
public static class ModelQueries
{
    public static StorageQuery Expand(ModelQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var where = query.Where.Select(Expand).ToArray();
        var orderBy = query.OrderBy is null
            ? Array.Empty<QuerySort>()
            : [new QuerySort(query.OrderBy, query.NewestFirst)];

        return new StorageQuery(query.Collection, where, orderBy, take: query.Limit);
    }

    private static QueryCondition Expand(ModelCondition condition) =>
        new(condition.Column, Operator(condition.Operator), condition.Values);

    private static QueryOperator Operator(ModelOperator @operator) => @operator switch
    {
        ModelOperator.Is => QueryOperator.Equals,
        ModelOperator.IsNot => QueryOperator.NotEquals,
        ModelOperator.Before => QueryOperator.LessThan,
        ModelOperator.After => QueryOperator.GreaterThan,
        ModelOperator.AtLeast => QueryOperator.GreaterOrEqual,
        ModelOperator.AtMost => QueryOperator.LessOrEqual,
        ModelOperator.Contains => QueryOperator.Contains,
        ModelOperator.OneOf => QueryOperator.In,
        ModelOperator.HasNothing => QueryOperator.IsNothing,
        ModelOperator.HasSomething => QueryOperator.IsSomething,
        _ => throw new InvalidDefinitionException("condition", $"'{@operator}' is not something that can be asked.")
    };
}
