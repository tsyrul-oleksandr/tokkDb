using System.Globalization;
using System.Text.Json.Nodes;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Typed values as text that comes back as the same typed value: what a held intent, a stored
/// query and a result handle are written with, so that a restart reads them back as what they
/// were (AG-8, QR-3a). A one-letter tag says the kind, for the same reason the engine's key
/// encoder tags every key: "2024" as text and as a number are different values.
/// </summary>
public static class Values
{
    public static string Encode(object? value) => value switch
    {
        null => "0",
        string text => "s" + text,
        long whole => "i" + whole.ToString(CultureInfo.InvariantCulture),
        int whole => "i" + whole.ToString(CultureInfo.InvariantCulture),
        decimal exact => "d" + exact.ToString(CultureInfo.InvariantCulture),
        bool flag => "b" + (flag ? "1" : "0"),
        DateOnly day => "y" + day.ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => "t" + moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset moment => "t" + moment.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        Ulid id => "u" + id,
        _ => "s" + value
    };

    public static object? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded == "0") return null;

        var rest = encoded[1..];

        return encoded[0] switch
        {
            's' => rest,
            'i' => long.Parse(rest, CultureInfo.InvariantCulture),
            'd' => decimal.Parse(rest, NumberStyles.Number, CultureInfo.InvariantCulture),
            'b' => rest == "1",
            'y' => DateOnly.Parse(rest, CultureInfo.InvariantCulture),
            't' => DateTime.SpecifyKind(DateTime.Parse(rest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc),
            'u' => Ulid.Parse(rest),
            _ => rest
        };
    }

    public static JsonObject EncodeFields(IReadOnlyDictionary<string, object?> fields)
    {
        var json = new JsonObject();
        foreach (var (name, value) in fields.OrderBy(static entry => entry.Key, StringComparer.Ordinal)) json[name] = Encode(value);
        return json;
    }

    public static Dictionary<string, object?> DecodeFields(JsonObject? json)
    {
        var fields = new Dictionary<string, object?>(Names.Comparer);
        if (json is null) return fields;
        foreach (var (name, value) in json) fields[name] = Decode(value?.ToString());
        return fields;
    }

    /// <summary>A value as a person reads it: no quotes, invariant, dates as days.</summary>
    public static string Show(object? value) => value switch
    {
        null => "",
        string text => text,
        bool flag => flag ? "yes" : "no",
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        decimal exact => exact.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    /// <summary>Text into a value of the column's type, by the same readers the files are read with.</summary>
    public static bool TryRead(ColumnType type, string? text, out object? value)
    {
        var kind = type switch
        {
            ColumnType.Integer => Ingestion.ValueKind.Integer,
            ColumnType.Decimal => Ingestion.ValueKind.Decimal,
            ColumnType.Boolean => Ingestion.ValueKind.Boolean,
            ColumnType.Date => Ingestion.ValueKind.Date,
            ColumnType.Timestamp => Ingestion.ValueKind.Timestamp,
            _ => Ingestion.ValueKind.Text
        };

        return Ingestion.ValueParsing.TryRead(kind, text, out value);
    }
}

/// <summary>
/// A <see cref="StorageQuery"/> as text and back (QR-3a, QR-3): what a result handle keeps, what
/// the refinement operation is shown, and what the trace's retrieval step records (TR-6).
/// </summary>
public static class QueryJson
{
    public static string Write(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var json = new JsonObject
        {
            ["thing"] = query.CollectionName,
            ["where"] = new JsonArray([.. query.Where.Select(condition => (JsonNode)new JsonObject
            {
                ["field"] = condition.ColumnName,
                ["operator"] = condition.Operator.ToString(),
                ["values"] = new JsonArray([.. condition.Values.Select(value => (JsonNode)Values.Encode(value))])
            })]),
            ["orderBy"] = new JsonArray([.. query.OrderBy.Select(sort => (JsonNode)new JsonObject { ["field"] = sort.ColumnName, ["descending"] = sort.Descending })]),
            ["take"] = query.Take,
            ["aggregates"] = new JsonArray([.. query.Aggregates.Select(aggregate => (JsonNode)new JsonObject { ["function"] = aggregate.Function.ToString(), ["field"] = aggregate.ColumnName })])
        };

        return json.ToJsonString();
    }

    public static StorageQuery Read(string text)
    {
        var json = JsonNode.Parse(text)?.AsObject() ?? throw new FormatException("That is not a stored query.");

        var where = (json["where"]?.AsArray() ?? []).Select(condition => new QueryCondition(
            condition!["field"]!.ToString(),
            Enum.Parse<QueryOperator>(condition["operator"]!.ToString()),
            [.. (condition["values"]?.AsArray() ?? []).Select(value => Values.Decode(value?.ToString()))])).ToList();

        var orderBy = (json["orderBy"]?.AsArray() ?? []).Select(sort => new QuerySort(sort!["field"]!.ToString(), sort["descending"]?.GetValue<bool>() ?? false)).ToList();

        var query = new StorageQuery(json["thing"]!.ToString(), where, orderBy, take: json["take"]?.GetValue<int?>());

        foreach (var aggregate in json["aggregates"]?.AsArray() ?? [])
        {
            var function = Enum.Parse<AggregateFunction>(aggregate!["function"]!.ToString());
            query = query.Computing(new QueryAggregate(function, function is AggregateFunction.Count ? null : aggregate["field"]?.ToString()));
        }

        return query;
    }

    /// <summary>The query as the refinement operation reads it: the same words the query operation writes.</summary>
    public static string Describe(StorageQuery query)
    {
        var lines = new List<string> { $"thing: {query.CollectionName}" };
        foreach (var condition in query.Where)
        {
            lines.Add($"where {condition.ColumnName} {Word(condition.Operator)} {string.Join(", ", condition.Values.Select(Values.Show))}".TrimEnd());
        }

        foreach (var sort in query.OrderBy) lines.Add($"order by {sort.ColumnName}{(sort.Descending ? ", largest first" : "")}");
        if (query.Take is { } take) lines.Add($"at most {take}");
        foreach (var aggregate in query.Aggregates.Where(static aggregate => aggregate.Function is AggregateFunction.Sum)) lines.Add($"total of {aggregate.ColumnName}");

        return string.Join("\n", lines);
    }

    private static string Word(QueryOperator @operator) => @operator switch
    {
        QueryOperator.Equals => "is",
        QueryOperator.NotEquals => "is not",
        QueryOperator.LessThan => "before",
        QueryOperator.LessOrEqual => "at most",
        QueryOperator.GreaterThan => "after",
        QueryOperator.GreaterOrEqual => "at least",
        QueryOperator.Contains => "contains",
        QueryOperator.StartsWith => "starts with",
        QueryOperator.EndsWith => "ends with",
        QueryOperator.In => "one of",
        QueryOperator.IsNothing => "has nothing",
        QueryOperator.IsSomething => "has something",
        _ => @operator.ToString()
    };
}
