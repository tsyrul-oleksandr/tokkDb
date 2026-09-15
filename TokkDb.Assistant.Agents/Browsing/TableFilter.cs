using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Browsing;

/// <summary>What a person can ask of a field from the table header (BR-4), in their words.</summary>
public enum FilterKind
{
    Is = 1,
    IsNot,
    Contains,
    Over,
    Under,
    OnOrAfter,
    OnOrBefore,
    IsEmpty,
    IsFilled
}

/// <summary>
/// One narrowing of the table (BR-4): a field, what is asked of it, and the value as typed. It
/// becomes the same declarative query the assistant uses (SC-7), executed with no model.
/// </summary>
public sealed record TableFilter(string Field, FilterKind Kind, string? Value = null)
{
    /// <summary>The filter as the table shows it, so that it is visible and removable: "cost over 500".</summary>
    public string Describe() => Kind switch
    {
        FilterKind.Is => $"{Plain} is {Value}",
        FilterKind.IsNot => $"{Plain} is not {Value}",
        FilterKind.Contains => $"{Plain} contains {Value}",
        FilterKind.Over => $"{Plain} over {Value}",
        FilterKind.Under => $"{Plain} under {Value}",
        FilterKind.OnOrAfter => $"{Plain} from {Value}",
        FilterKind.OnOrBefore => $"{Plain} up to {Value}",
        FilterKind.IsEmpty => $"{Plain} is empty",
        FilterKind.IsFilled => $"{Plain} is filled in",
        _ => Plain
    };

    private string Plain => WhatItKeeps.Plain(Field);

    /// <summary>The kinds that make sense for a field of that type: nobody is offered "over" for a name.</summary>
    public static IReadOnlyList<FilterKind> KindsFor(ColumnType type) => type switch
    {
        ColumnType.Text => [FilterKind.Contains, FilterKind.Is, FilterKind.IsNot, FilterKind.IsEmpty, FilterKind.IsFilled],
        ColumnType.Boolean => [FilterKind.Is, FilterKind.IsEmpty, FilterKind.IsFilled],
        ColumnType.Date or ColumnType.Timestamp => [FilterKind.Is, FilterKind.OnOrAfter, FilterKind.OnOrBefore, FilterKind.IsEmpty, FilterKind.IsFilled],
        _ => [FilterKind.Is, FilterKind.Over, FilterKind.Under, FilterKind.IsNot, FilterKind.IsEmpty, FilterKind.IsFilled]
    };

    /// <summary>The condition, with the value read as the field keeps it; null when the value does not read.</summary>
    public QueryCondition? ToCondition(CollectionDefinition definition, out string? problem)
    {
        problem = null;
        var column = definition.Column(Field);
        if (column is null)
        {
            problem = $"There is no field called {Plain}.";
            return null;
        }

        if (Kind is FilterKind.IsEmpty) return new QueryCondition(column.Name, QueryOperator.IsNothing);
        if (Kind is FilterKind.IsFilled) return new QueryCondition(column.Name, QueryOperator.IsSomething);

        if (!Values.TryRead(column.Type, Value, out var value) || value is null)
        {
            problem = $"\"{Value}\" is not {WhatItKeeps.Kind(column.Type)}, which is what {Plain} keeps.";
            return null;
        }

        var @operator = Kind switch
        {
            FilterKind.Is => QueryOperator.Equals,
            FilterKind.IsNot => QueryOperator.NotEquals,
            FilterKind.Contains => column.Type is ColumnType.Text ? QueryOperator.Contains : QueryOperator.Equals,
            FilterKind.Over => QueryOperator.GreaterThan,
            FilterKind.Under => QueryOperator.LessThan,
            FilterKind.OnOrAfter => QueryOperator.GreaterOrEqual,
            FilterKind.OnOrBefore => QueryOperator.LessOrEqual,
            _ => QueryOperator.Equals
        };

        return new QueryCondition(column.Name, @operator, value);
    }
}
