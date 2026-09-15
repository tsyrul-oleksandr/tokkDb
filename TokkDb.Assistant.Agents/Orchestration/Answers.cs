using System.Text.Json;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>What the query operation answered, before it is turned into a query (QR-1).</summary>
public sealed record QueryAnswer(
    string Thing,
    IReadOnlyList<(string Field, string Operator, IReadOnlyList<string> Values)> Where,
    string? OrderBy,
    bool LargestFirst,
    int Limit,
    string? Total);

/// <summary>What the structure operation answered.</summary>
public sealed record StructureAnswer(string Action, string Thing, string Field, string NewName, string Kind);

/// <summary>What the correction operation answered.</summary>
public sealed record CorrectionAnswer(int Which, string Field, string Value);

/// <summary>
/// The parsers of the model's answers (AG-5, R-2): each reads the JSON and re-checks in C# every
/// bound the grammar cannot enforce - a choice that was offered, a field that exists, a value
/// that reads as the field's kind - and says what was wrong in words the model can act on.
/// </summary>
public static class Answers
{
    public static Parsed<string> Intent(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var intent = json.RootElement.TryGetProperty("intent", out var property) ? property.GetString() : null;
            return intent is "store" or "find" or "correct" or "remove" or "restructure" or "undo" or "other"
                ? Parsed<string>.Ok(intent)
                : Parsed<string>.Invalid("intent has to be one of store, find, correct, remove, restructure, undo, other");
        }
        catch (JsonException failure)
        {
            return Parsed<string>.Invalid("not JSON: " + failure.Message);
        }
    }

    public static Parsed<(string Kind, IReadOnlyList<IReadOnlyList<(string Name, string Value, string Kind)>> Records)> Extraction(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var root = json.RootElement;
            var kind = root.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() ?? "" : "";
            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            {
                return Parsed<(string, IReadOnlyList<IReadOnlyList<(string, string, string)>>)>.Invalid("no records array");
            }

            var parsed = new List<IReadOnlyList<(string, string, string)>>();
            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array) continue;

                var list = new List<(string, string, string)>();
                foreach (var field in fields.EnumerateArray())
                {
                    var name = field.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var value = field.TryGetProperty("value", out var v) ? v.ToString() : null;
                    var word = field.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) return Parsed<(string, IReadOnlyList<IReadOnlyList<(string, string, string)>>)>.Invalid("a field has no name");
                    if (SchemaDigest.Type(word) is null) return Parsed<(string, IReadOnlyList<IReadOnlyList<(string, string, string)>>)>.Invalid($"the kind of {name} has to be one of {Catalogue.KindWords}");
                    list.Add((name.Trim(), value ?? "", word!));
                }

                if (list.Count > 0) parsed.Add(list);
            }

            return Parsed<(string, IReadOnlyList<IReadOnlyList<(string, string, string)>>)>.Ok((string.IsNullOrWhiteSpace(kind) ? "things" : kind.Trim(), parsed));
        }
        catch (JsonException failure)
        {
            return Parsed<(string, IReadOnlyList<IReadOnlyList<(string, string, string)>>)>.Invalid("not JSON: " + failure.Message);
        }
    }

    /// <summary>AG-3c: the choice has to be one of the offered names, or "none"; it cannot be a name that was not offered.</summary>
    public static OutputParser<MappingAnswer> Mapping(IReadOnlyList<PlacementCandidate> shortlist) => text =>
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var root = json.RootElement;
            var choice = root.TryGetProperty("choice", out var choiceProperty) ? choiceProperty.GetString()?.Trim() ?? "" : "";

            if (choice.Length > 0 && !string.Equals(choice, "none", StringComparison.OrdinalIgnoreCase)
                && !shortlist.Any(candidate => Names.Same(candidate.Definition.Name, choice)))
            {
                return Parsed<MappingAnswer>.Invalid($"'{choice}' was not offered; choose one of {string.Join(", ", shortlist.Select(static candidate => candidate.Definition.Name))} or \"none\"");
            }

            var fields = new List<(string, string)>();
            if (root.TryGetProperty("fields", out var mapped) && mapped.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in mapped.EnumerateArray())
                {
                    var incoming = field.TryGetProperty("incoming", out var i) ? i.GetString() : null;
                    var existing = field.TryGetProperty("existing", out var e) ? e.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(incoming)) fields.Add((incoming.Trim(), existing?.Trim() ?? ""));
                }
            }

            return Parsed<MappingAnswer>.Ok(new MappingAnswer(
                choice,
                root.TryGetProperty("newName", out var newName) ? newName.GetString() : null,
                root.TryGetProperty("purpose", out var purpose) ? purpose.GetString() : null,
                fields));
        }
        catch (JsonException failure)
        {
            return Parsed<MappingAnswer>.Invalid("not JSON: " + failure.Message);
        }
    };

    /// <summary>QR-1: a query naming a thing or a field that does not exist is refused before execution, naming it.</summary>
    public static OutputParser<QueryAnswer> Query(IReadOnlyCollection<CollectionDefinition> definitions) => text =>
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var root = json.RootElement;
            var thing = root.TryGetProperty("thing", out var thingProperty) ? thingProperty.GetString()?.Trim() ?? "" : "";
            var definition = definitions.FirstOrDefault(candidate => Names.Same(candidate.Name, thing))
                             ?? definitions.FirstOrDefault(candidate => string.Equals(candidate.Name, thing, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                return Parsed<QueryAnswer>.Invalid($"'{thing}' is not one of the things stored; the names are {string.Join(", ", definitions.Select(static candidate => candidate.Name))}");
            }

            var where = new List<(string, string, IReadOnlyList<string>)>();
            if (root.TryGetProperty("where", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
            {
                foreach (var condition in conditions.EnumerateArray())
                {
                    var field = condition.TryGetProperty("field", out var f) ? f.GetString()?.Trim() ?? "" : "";
                    var column = definition.Column(field) ?? definition.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.OrdinalIgnoreCase));
                    if (column is null) return Parsed<QueryAnswer>.Invalid($"'{definition.Name}' has no field called '{field}'; its fields are {string.Join(", ", definition.Columns.Select(static column => column.Name))}");

                    var @operator = condition.TryGetProperty("operator", out var o) ? o.GetString()?.Trim().ToLowerInvariant() ?? "" : "";
                    var values = condition.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                        ? v.EnumerateArray().Select(static value => value.ToString()).ToList()
                        : [];

                    if (@operator is not ("has nothing" or "has something"))
                    {
                        foreach (var value in values)
                        {
                            if (!Values.TryRead(column.Type, value, out _))
                            {
                                return Parsed<QueryAnswer>.Invalid($"'{value}' is not {SchemaDigest.Kind(column.Type)}, which is what {column.Name} keeps");
                            }
                        }

                        if (values.Count == 0) return Parsed<QueryAnswer>.Invalid($"the condition on {column.Name} has no value");
                    }

                    where.Add((column.Name, @operator, values));
                }
            }

            var orderBy = root.TryGetProperty("orderBy", out var orderProperty) ? orderProperty.GetString()?.Trim() : null;
            if (!string.IsNullOrEmpty(orderBy) && definition.Column(orderBy) is null)
            {
                var column = definition.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, orderBy, StringComparison.OrdinalIgnoreCase));
                if (column is null) return Parsed<QueryAnswer>.Invalid($"'{definition.Name}' has no field called '{orderBy}' to order by");
                orderBy = column.Name;
            }

            var total = root.TryGetProperty("total", out var totalProperty) ? totalProperty.GetString()?.Trim() : null;
            if (!string.IsNullOrEmpty(total))
            {
                var column = definition.Column(total) ?? definition.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, total, StringComparison.OrdinalIgnoreCase));
                if (column is null) return Parsed<QueryAnswer>.Invalid($"'{definition.Name}' has no field called '{total}' to add up");
                if (column.Type is not (ColumnType.Integer or ColumnType.Decimal)) return Parsed<QueryAnswer>.Invalid($"{column.Name} keeps {SchemaDigest.Kind(column.Type)}, which cannot be added up");
                total = column.Name;
            }

            return Parsed<QueryAnswer>.Ok(new QueryAnswer(
                definition.Name,
                where,
                string.IsNullOrEmpty(orderBy) ? null : orderBy,
                root.TryGetProperty("largestFirst", out var largest) && largest.ValueKind == JsonValueKind.True,
                root.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var count) ? Math.Max(0, count) : 0,
                string.IsNullOrEmpty(total) ? null : total));
        }
        catch (JsonException failure)
        {
            return Parsed<QueryAnswer>.Invalid("not JSON: " + failure.Message);
        }
    };

    public static OutputParser<StructureAnswer> Structure(IReadOnlyCollection<CollectionDefinition> definitions) => text =>
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var root = json.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString()?.Trim() ?? "none" : "none";
            var thing = root.TryGetProperty("thing", out var t) ? t.GetString()?.Trim() ?? "" : "";
            var field = root.TryGetProperty("field", out var f) ? f.GetString()?.Trim() ?? "" : "";

            if (action == "none") return Parsed<StructureAnswer>.Ok(new StructureAnswer(action, thing, field, "", ""));

            var definition = definitions.FirstOrDefault(candidate => Names.Same(candidate.Name, thing))
                             ?? definitions.FirstOrDefault(candidate => string.Equals(candidate.Name, thing, StringComparison.OrdinalIgnoreCase));
            if (definition is null) return Parsed<StructureAnswer>.Invalid($"'{thing}' is not one of the things stored; the names are {string.Join(", ", definitions.Select(static candidate => candidate.Name))}");

            if (action is "remove-field" or "rename-field" or "retype-field" or "set-unique" or "set-required")
            {
                var column = definition.Column(field) ?? definition.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.OrdinalIgnoreCase));
                if (column is null) return Parsed<StructureAnswer>.Invalid($"'{definition.Name}' has no field called '{field}'; its fields are {string.Join(", ", definition.Columns.Select(static column => column.Name))}");
                field = column.Name;
            }

            var kind = root.TryGetProperty("kind", out var k) ? k.GetString()?.Trim() ?? "" : "";
            if (action is "retype-field" or "add-field" && SchemaDigest.Type(kind) is null)
            {
                return Parsed<StructureAnswer>.Invalid($"the kind has to be one of {Catalogue.KindWords}");
            }

            return Parsed<StructureAnswer>.Ok(new StructureAnswer(action, definition.Name, field,
                root.TryGetProperty("newName", out var n) ? n.GetString()?.Trim() ?? "" : "", kind));
        }
        catch (JsonException failure)
        {
            return Parsed<StructureAnswer>.Invalid("not JSON: " + failure.Message);
        }
    };

    public static OutputParser<CorrectionAnswer> Correction(int shown, CollectionDefinition definition) => text =>
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var root = json.RootElement;
            var which = root.TryGetProperty("which", out var w) && w.TryGetInt32(out var number) ? number : 0;
            if (which < 1 || which > shown) return Parsed<CorrectionAnswer>.Invalid($"which has to be a number from 1 to {shown}");

            var field = root.TryGetProperty("field", out var f) ? f.GetString()?.Trim() ?? "" : "";
            var column = definition.Column(field) ?? definition.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.OrdinalIgnoreCase));
            if (column is null) return Parsed<CorrectionAnswer>.Invalid($"'{definition.Name}' has no field called '{field}'; its fields are {string.Join(", ", definition.Columns.Select(static column => column.Name))}");

            var value = root.TryGetProperty("value", out var v) ? v.ToString() : "";
            if (!Values.TryRead(column.Type, value, out _)) return Parsed<CorrectionAnswer>.Invalid($"'{value}' is not {SchemaDigest.Kind(column.Type)}, which is what {column.Name} keeps");

            return Parsed<CorrectionAnswer>.Ok(new CorrectionAnswer(which, column.Name, value));
        }
        catch (JsonException failure)
        {
            return Parsed<CorrectionAnswer>.Invalid("not JSON: " + failure.Message);
        }
    };

    public static OutputParser<string> Text(string property) => text =>
    {
        try
        {
            using var json = JsonDocument.Parse(Extract(text));
            var value = json.RootElement.TryGetProperty(property, out var p) ? p.GetString() : null;
            return string.IsNullOrWhiteSpace(value) ? Parsed<string>.Invalid($"no {property}") : Parsed<string>.Ok(value.Trim());
        }
        catch (JsonException failure)
        {
            return Parsed<string>.Invalid("not JSON: " + failure.Message);
        }
    };

    /// <summary>The outermost JSON object in the text: a thinking model may wrap or precede its answer.</summary>
    public static string Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var start = text.IndexOf('{');
        if (start < 0) return text;
        var depth = 0; var inString = false; var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') inString = false; continue; }
            if (c == '"') inString = true; else if (c == '{') depth++; else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return text[start..];
    }
}
