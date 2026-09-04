using System.ComponentModel;

namespace TokkDb.LLM.Core;

/// <summary>
/// Tool-facing query model.
///
/// This layer only proxies conditions: it carries names and text exactly as the
/// model wrote them and performs no resolution, no coercion and no validation.
/// The descriptions on each member are what the agent sees in the tool schema,
/// so they are written for a model rather than for a developer.
///
/// Names are turned into column and relation definitions by the binder, and the
/// resulting storage query is validated and executed by the storage layer.
/// </summary>
public sealed class RecordQuery
{
    [Description("Name of the collection to search.")]
    public string CollectionName { get; set; } = string.Empty;

    [Description(
        "Optional ids of specific records to fetch. Use this to look a record up by its id; " +
        "omit it to search the whole collection.")]
    public List<string>? RecordIds { get; set; }

    [Description("Optional condition. Omit to match every record.")]
    public RecordFilter? Where { get; set; }

    [Description("Optional sort order, applied in sequence.")]
    public List<RecordQuerySort>? OrderBy { get; set; }

    [Description("Number of records to skip. Defaults to 0.")]
    public int? Skip { get; set; }

    [Description("Maximum number of records to return. Defaults to 10; ask for more only when they are needed.")]
    public int? Take { get; set; }

    [Description("Columns to return. Omit to return every column.")]
    public List<string>? Select { get; set; }
}

public sealed class RecordQuerySort
{
    [Description("Column to sort by.")]
    public string Column { get; set; } = string.Empty;

    [Description("Sort direction: 'asc' or 'desc'. Defaults to 'asc'.")]
    public string? Direction { get; set; }
}

/// <summary>
/// One condition. Use exactly one of the three forms: a field comparison, a
/// logical group, or a step across a relation.
/// </summary>
public sealed class RecordFilter
{
    [Description("Field form: the column to compare. Use with 'operator'.")]
    public string? Field { get; set; }

    [Description(
        "Field form: how to compare. One of eq, neq, gt, gte, lt, lte, startsWith, " +
        "endsWith, contains, in, between, isNull, isNotNull. " +
        "startsWith, endsWith and contains work only on text columns; " +
        "gt, gte, lt, lte and between only on number and date columns.")]
    public string? Operator { get; set; }

    [Description("Field form: the value to compare against. Numbers may be written as text.")]
    public string? Value { get; set; }

    [Description("Field form: values for 'in' (one or more) and 'between' (exactly two).")]
    public List<string>? Values { get; set; }

    [Description("Group form: 'and', 'or' or 'not'. Use with 'filters'.")]
    public string? Logic { get; set; }

    [Description("Group form: the conditions to combine. 'not' takes exactly one.")]
    public List<RecordFilter>? Filters { get; set; }

    [Description(
        "Relation form: the name of a declared relation to follow. " +
        "Only declared relations can be followed; check the collection schema for their names.")]
    public string? Relation { get; set; }

    [Description(
        "Relation form: 'any' when at least one related record must match, " +
        "'none' when no related record may match, 'all' when every related record must match. " +
        "Defaults to 'any'.")]
    public string? Quantifier { get; set; }

    [Description("Relation form: the condition applied to the related records.")]
    public RecordFilter? Where { get; set; }
}

public sealed record RecordQueryRow(
    string RecordId,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record RecordQueryResult(
    string CollectionName,
    IReadOnlyList<RecordQueryRow> Rows,
    int Skip,
    int Take,
    int Returned);
