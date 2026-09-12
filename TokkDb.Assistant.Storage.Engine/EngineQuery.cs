using TokkDb.Assistant.Storage;
using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Pages.Query;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// A validated query as the planner takes it, and what the planner chose as the contract reports
/// it.
///
/// Every condition becomes a conjunct - a column, an operator and constants, with nothing
/// referring to the record - which is the shape the planner can match against an index without
/// reading any data. There is no residual, ever, and that is what the query type's refusal to
/// carry an OR buys: a residual is evaluated against every record the access path returns, so a
/// query that has one reads everything whatever the report says about the path it took.
/// </summary>
internal static class EngineQuery
{
    public static NormalizedQuery ToNormalized(ValidatedQuery query) =>
        // null! rather than null: the engine predates nullable reference types and declares the
        // residual non-nullable while documenting that null means there is none.
        new([.. query.Where.SelectMany(ToConjuncts)], Residual: null!);

    /// <summary>
    /// Usually one conjunct, and for most operators two.
    ///
    /// The second is "and this column has a value". The engine compares a column a record has no
    /// value for as though it held null, so <c>city != 'Prague'</c> is true of a record that was
    /// never given a city - which is the ordinary three-valued answer and not the one this
    /// contract gives. Saying it as an extra conjunct keeps the whole query in the planner's
    /// hands: it is an ordinary comparison against null, an index can answer it, and nothing has
    /// to be re-checked afterwards in C#.
    /// </summary>
    private static IEnumerable<QueryPredicate> ToConjuncts(ValidatedCondition condition)
    {
        yield return ToConjunct(condition);

        if (condition.Operator is QueryOperator.IsNothing or QueryOperator.IsSomething)
        {
            yield break;
        }

        yield return new QueryPredicate(
            condition.Column.Name,
            ComparisonOperator.NotEqual,
            EngineValues.TypeOf(condition.Column.Type),
            [new TokkDb.Documents.Values.NullDocumentValue()]);
    }

    private static QueryPredicate ToConjunct(ValidatedCondition condition)
    {
        var type = EngineValues.TypeOf(condition.Column.Type);

        // "Has no value" and "has a value" are comparisons against null rather than operators of
        // their own, which is what lets an index answer them: a null is an ordinary key.
        if (condition.Operator is QueryOperator.IsNothing or QueryOperator.IsSomething)
        {
            return new QueryPredicate(
                condition.Column.Name,
                condition.Operator is QueryOperator.IsNothing
                    ? ComparisonOperator.Equal
                    : ComparisonOperator.NotEqual,
                type,
                [new TokkDb.Documents.Values.NullDocumentValue()]);
        }

        return new QueryPredicate(
            condition.Column.Name,
            ToComparison(condition.Operator),
            type,
            [.. condition.Values.Select(value => EngineValues.ToDocument(condition.Column.Type, value))]);
    }

    private static ComparisonOperator ToComparison(QueryOperator @operator) => @operator switch
    {
        QueryOperator.Equals => ComparisonOperator.Equal,
        QueryOperator.NotEquals => ComparisonOperator.NotEqual,
        QueryOperator.LessThan => ComparisonOperator.Less,
        QueryOperator.LessOrEqual => ComparisonOperator.LessOrEqual,
        QueryOperator.GreaterThan => ComparisonOperator.Greater,
        QueryOperator.GreaterOrEqual => ComparisonOperator.GreaterOrEqual,
        QueryOperator.StartsWith => ComparisonOperator.StartsWith,
        QueryOperator.EndsWith => ComparisonOperator.EndsWith,
        QueryOperator.Contains => ComparisonOperator.Contains,
        QueryOperator.In => ComparisonOperator.In,
        _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "No comparison for this operator.")
    };

    /// <summary>
    /// The engine's access path, as the contract describes one. The engine's own description is
    /// carried through rather than rewritten, because it is what the engine's diagnostics say
    /// and two spellings of the same plan in two places would eventually disagree.
    /// </summary>
    public static QueryAccessPath ToAccessPath(AccessPath path) => path switch
    {
        IndexSeekPath seek => new QueryAccessPath(
            QueryAccessPathKind.IndexSeek, seek.CollectionName, seek.ColumnName, seek.Describe()),

        IndexRangePath range => new QueryAccessPath(
            QueryAccessPathKind.IndexRange, range.CollectionName, range.ColumnName, range.Describe()),

        PrimaryKeyPath identity => new QueryAccessPath(
            QueryAccessPathKind.IdentityLookup, identity.CollectionName, null, identity.Describe()),

        _ => new QueryAccessPath(
            QueryAccessPathKind.FullScan, path.CollectionName, null, path.Describe())
    };
}
