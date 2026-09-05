using System.Globalization;
using TokkDb.Documents;
using TokkDb.LLM.Core;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// DC-5: one query representation. <see cref="StorageQuery"/> is the logical input — the
/// only thing a caller writes — and the engine's expression tree is the internal form.
/// This is the one place the two meet, and it is here rather than in either of them
/// because it is the only project that can see both.
///
/// The engine gains no knowledge of <c>ColumnDefinition</c> and the application gains none
/// of the expression tree, which is what keeps the count at one parser rather than two.
/// </summary>
public static class StorageQueryTranslator
{
    /// <summary>
    /// A query in the engine's terms: the predicate as an expression tree, that tree split
    /// into conjuncts and a residual, and the parts of the query that are not a predicate
    /// at all carried through unchanged.
    /// </summary>
    public sealed record TranslatedQuery(
        string CollectionName,
        IExpression Where,
        NormalizedQuery Normalized,
        IReadOnlyList<Ulid> Ids,
        IReadOnlyList<(string Column, bool Descending)> OrderBy,
        int Skip,
        int Take,
        IReadOnlyList<string> Select);

    public static TranslatedQuery Translate(StorageQuery query)
    {
        var where = query.Where is null ? null : TranslateFilter(query.Where);
        return new TranslatedQuery(
            query.Collection.Name,
            where,
            QueryNormalizer.Normalize(where),
            query.Ids ?? [],
            query.OrderBy.Select(sort => (sort.Column.Name, sort.Descending)).ToArray(),
            query.Skip,
            query.Take,
            query.Select.Select(column => column.Name).ToArray());
    }

    private static IExpression TranslateFilter(StorageFilter filter) => filter switch
    {
        StorageFieldFilter field => TranslateField(field),
        StorageGroupFilter group => TranslateGroup(group),
        StorageNotFilter not => new NotExpression(TranslateFilter(not.Inner)),
        StorageRelationFilter relation => TranslateRelation(relation),
        _ => throw new NotSupportedException($"Filter '{filter.GetType().Name}' has no engine expression.")
    };

    private static IExpression TranslateGroup(StorageGroupFilter group)
    {
        var operands = group.Filters.Select(TranslateFilter).ToArray();
        return group.IsOr ? new OrExpression(operands) : new AndExpression(operands);
    }

    private static IExpression TranslateRelation(StorageRelationFilter relation) =>
        new RelationStepExpression(
            relation.Relation.Name,
            relation.SourceColumn.Name,
            relation.TargetCollection.Name,
            relation.TargetColumn.Name,
            relation.Quantifier switch
            {
                QueryQuantifier.None => RelationQuantifier.None,
                QueryQuantifier.All => RelationQuantifier.All,
                _ => RelationQuantifier.Any
            },
            relation.Inner is null ? null : TranslateFilter(relation.Inner));

    /// <summary>
    /// The operators that are not one comparison become the comparisons they stand for.
    /// <c>between</c> is two, so it normalises into two conjuncts an index can use from
    /// either end; <c>isNull</c> is an equality against null. <c>in</c> stays whole,
    /// because a set of equalities is a shape an index can answer and an OR of them is not.
    /// </summary>
    private static IExpression TranslateField(StorageFieldFilter filter)
    {
        var type = ToValueType(filter.Column.Type);
        var column = Column(filter.Column.Name);

        switch (filter.Operator)
        {
            case QueryOperator.Between:
                return new AndExpression([
                    Compare(filter, ComparisonOperator.GreaterOrEqual, type, Operand(filter, 0)),
                    Compare(filter, ComparisonOperator.LessOrEqual, type, Operand(filter, 1))
                ]);
            case QueryOperator.IsNull:
                return new ComparisonExpression(column, ComparisonOperator.Equal,
                    new ConstantExpression(new NullDocumentValue()), type);
            case QueryOperator.IsNotNull:
                return new ComparisonExpression(column, ComparisonOperator.NotEqual,
                    new ConstantExpression(new NullDocumentValue()), type);
            case QueryOperator.In:
                return new ComparisonExpression(column, ComparisonOperator.In,
                    new ConstantExpression(filter.Operands.Select(operand => ToValue(filter.Column, operand))
                        .ToArray()), type);
            default:
                return Compare(filter, ToComparison(filter.Operator), type, Operand(filter, 0));
        }
    }

    private static ComparisonExpression Compare(StorageFieldFilter filter, ComparisonOperator op,
        ValueTypeEnum type, IDocumentValue operand) =>
        new(Column(filter.Column.Name), op, new ConstantExpression(operand), type);

    /// <summary>
    /// A bare column read from the root of the record. Normalisation recognises exactly this
    /// shape as a column and treats anything deeper as a path into a document, which names
    /// no column an index could be chosen by.
    /// </summary>
    private static IExpression Column(string name) =>
        new PropertyExpression(name) { Parent = new RootExpression() };

    private static IDocumentValue Operand(StorageFieldFilter filter, int index) =>
        ToValue(filter.Column, index < filter.Operands.Count ? filter.Operands[index] : null);

    /// <summary>
    /// The operand text as the engine stores a value of that column's type. It has to match
    /// what <see cref="FieldMapSerializer"/> writes, or a query would be comparing against a
    /// form no record is in — including the four types the document format still has no
    /// value for, which are stored as invariant text.
    /// </summary>
    private static IDocumentValue ToValue(ColumnDefinition column, string? operand)
    {
        if (operand is null)
        {
            return new NullDocumentValue();
        }
        return column.Type switch
        {
            ColumnType.String => new StringDocumentValue(operand),
            ColumnType.Boolean => new BooleanDocumentValue(bool.Parse(operand)),
            ColumnType.Int32 => new IntDocumentValue(int.Parse(operand, CultureInfo.InvariantCulture)),
            ColumnType.Int64 => Text(long.Parse(operand, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture)),
            ColumnType.Decimal => Text(decimal.Parse(operand, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture)),
            ColumnType.DateTime => Text(DateTime.Parse(operand, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).ToString("O", CultureInfo.InvariantCulture)),
            ColumnType.Guid => Text(Guid.Parse(operand).ToString("D")),
            _ => throw new NotSupportedException($"Column type '{column.Type}' has no engine value.")
        };
    }

    private static IDocumentValue Text(string value) => new StringDocumentValue(value);

    private static ComparisonOperator ToComparison(QueryOperator op) => op switch
    {
        QueryOperator.Equals => ComparisonOperator.Equal,
        QueryOperator.NotEquals => ComparisonOperator.NotEqual,
        QueryOperator.GreaterThan => ComparisonOperator.Greater,
        QueryOperator.GreaterOrEqual => ComparisonOperator.GreaterOrEqual,
        QueryOperator.LessThan => ComparisonOperator.Less,
        QueryOperator.LessOrEqual => ComparisonOperator.LessOrEqual,
        QueryOperator.StartsWith => ComparisonOperator.StartsWith,
        QueryOperator.EndsWith => ComparisonOperator.EndsWith,
        QueryOperator.Contains => ComparisonOperator.Contains,
        _ => throw new NotSupportedException($"Operator '{op}' has no single comparison.")
    };

    private static ValueTypeEnum ToValueType(ColumnType type) => type switch
    {
        ColumnType.String => ValueTypeEnum.String,
        ColumnType.Boolean => ValueTypeEnum.Boolean,
        ColumnType.Int32 => ValueTypeEnum.Int,
        ColumnType.Int64 => ValueTypeEnum.Long,
        ColumnType.Decimal => ValueTypeEnum.Decimal,
        ColumnType.DateTime => ValueTypeEnum.DateTime,
        ColumnType.Guid => ValueTypeEnum.Guid,
        _ => throw new NotSupportedException($"Column type '{type}' has no engine value type.")
    };
}
