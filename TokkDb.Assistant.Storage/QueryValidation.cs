namespace TokkDb.Assistant.Storage;

/// <summary>
/// A query judged against the schema, with the columns resolved. SC-7's "validated before
/// execution", and shared so that a query does not mean two things.
///
/// The judgement is the same one a write gets, for the same reason: a condition's value is a
/// value of its column, and a caller that may store 840.50 in a decimal column and may not
/// store "840.50" in one should not be able to search for the second either.
/// </summary>
internal sealed record ValidatedCondition(
    ColumnDefinition Column,
    QueryOperator Operator,
    IReadOnlyList<object?> Values);

internal sealed record ValidatedQuery(
    CollectionDefinition Definition,
    StorageQuery Query,
    IReadOnlyList<ValidatedCondition> Where,
    IReadOnlyList<(ColumnDefinition Column, bool Descending)> OrderBy);

internal static class QueryValidation
{
    /// <summary>
    /// Resolves every column the query names and checks that what is asked of it can be asked.
    /// </summary>
    /// <exception cref="StorageValidationException">
    /// Thrown with every reason the query did not fit, not only the first - a model that got one
    /// column wrong usually got two, and one round trip is cheaper than two.
    /// </exception>
    public static ValidatedQuery Against(CollectionDefinition definition, StorageQuery query)
    {
        var errors = new List<StorageError>();
        var where = new List<ValidatedCondition>(query.Where.Count);
        var order = new List<(ColumnDefinition, bool)>(query.OrderBy.Count);

        foreach (var condition in query.Where)
        {
            var column = definition.Column(condition.ColumnName);
            if (column is null)
            {
                errors.Add(new UnknownColumn(definition.Name, condition.ColumnName));
                continue;
            }

            if (!Suits(condition.Operator, column.Type))
            {
                errors.Add(new OperatorNotSuitable(definition.Name, column.Name, condition.Operator, column.Type));
                continue;
            }

            if (!CountFits(condition, out var wanted))
            {
                errors.Add(new OperandCountWrong(
                    definition.Name, column.Name, condition.Operator, wanted, condition.Values.Count));
                continue;
            }

            var values = new List<object?>(condition.Values.Count);
            var fits = true;

            foreach (var offered in condition.Values)
            {
                // Nothing is not a value to compare against. A caller that means "this column
                // has no value" says so with IsNothing, which is a different question and one an
                // index can answer; guessing that a null operand meant that would be the storage
                // deciding what a model meant, which is the mapping step's job and not this one.
                if (offered is null || !ColumnTypes.TryCanonicalise(column.Type, offered, out var value))
                {
                    errors.Add(new ColumnTypeMismatch(definition.Name, column.Name, column.Type, offered));
                    fits = false;
                    continue;
                }

                values.Add(value);
            }

            if (fits)
            {
                where.Add(new ValidatedCondition(column, condition.Operator, values));
            }
        }

        foreach (var sort in query.OrderBy)
        {
            var column = definition.Column(sort.ColumnName);
            if (column is null)
            {
                errors.Add(new UnknownSortColumn(definition.Name, sort.ColumnName));
                continue;
            }

            order.Add((column, sort.Descending));
        }

        if (errors.Count > 0)
        {
            throw new StorageValidationException(errors);
        }

        return new ValidatedQuery(definition, query, where, order);
    }

    private static bool Suits(QueryOperator @operator, ColumnType type) => @operator switch
    {
        QueryOperator.Equals or QueryOperator.NotEquals or QueryOperator.In
            or QueryOperator.IsNothing or QueryOperator.IsSomething => true,

        // A true-or-false has two values and no order between them worth asking about. Every
        // other type does: text ordinally, the numbers numerically, the dates chronologically.
        QueryOperator.LessThan or QueryOperator.LessOrEqual
            or QueryOperator.GreaterThan or QueryOperator.GreaterOrEqual => type is not ColumnType.Boolean,

        QueryOperator.StartsWith or QueryOperator.EndsWith or QueryOperator.Contains => type is ColumnType.Text,

        _ => false
    };

    private static bool CountFits(QueryCondition condition, out int wanted)
    {
        switch (condition.Operator)
        {
            case QueryOperator.IsNothing or QueryOperator.IsSomething:
                wanted = 0;
                return condition.Values.Count == 0;

            case QueryOperator.In:
                wanted = 1;
                return condition.Values.Count >= 1;

            default:
                wanted = 1;
                return condition.Values.Count == 1;
        }
    }
}
