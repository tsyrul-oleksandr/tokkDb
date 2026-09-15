using System.Globalization;
using System.Text;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Context;

/// <summary>
/// The storage as a model sees it (§6.1, AG-6a): one line per thing stored - name, purpose,
/// field:kind - and one per relation, bounded to a budget.
///
/// <b>Bounded by the budget, not by the storage.</b> A storage of two hundred things renders as
/// the ones that fit, most recently changed first, then a count of the rest with their names
/// while those fit too. The order is stable for a stable storage, which is what makes the prefix
/// byte-identical between two calls (AG-6a): nothing here depends on the message.
///
/// The kinds are the words the operations ask the model to use, so that a field described as
/// "number" here is what the model says "number" about there.
/// </summary>
public static class SchemaDigest
{
    /// <summary>A kind in the model's words.</summary>
    public static string Kind(ColumnType type) => type switch
    {
        ColumnType.Text => "text",
        ColumnType.Integer => "whole number",
        ColumnType.Decimal => "number",
        ColumnType.Boolean => "true or false",
        ColumnType.Date => "date",
        ColumnType.Timestamp => "date and time",
        _ => type.ToString().ToLowerInvariant()
    };

    /// <summary>The model's word for a kind back into a type, or null for a word that is not one.</summary>
    public static ColumnType? Type(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "text" => ColumnType.Text,
        "whole number" => ColumnType.Integer,
        "number" => ColumnType.Decimal,
        "true or false" => ColumnType.Boolean,
        "date" => ColumnType.Date,
        "date and time" => ColumnType.Timestamp,
        _ => null
    };

    /// <summary>The kind an ingestion profile inferred, as a storage type.</summary>
    public static ColumnType Type(ValueKind kind) => kind switch
    {
        ValueKind.Integer => ColumnType.Integer,
        ValueKind.Decimal => ColumnType.Decimal,
        ValueKind.Boolean => ColumnType.Boolean,
        ValueKind.Date => ColumnType.Date,
        ValueKind.Timestamp => ColumnType.Timestamp,
        _ => ColumnType.Text
    };

    /// <summary>One thing, on one line.</summary>
    public static string Line(CollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var fields = string.Join(", ", definition.Columns
            .Where(column => !Names.Same(column.Name, Fingerprints.ColumnName))
            .Select(column => column.Name + ":" + Kind(column.Type)
                                     + (column.Required ? ", always needed" : "")
                                     + (column.Unique ? ", no two the same" : "")));

        return $"- {definition.Name}: {definition.Purpose ?? "no description"} [{fields}]";
    }

    /// <summary>
    /// The digest, within the budget. <paramref name="lastChanged"/> says which things go first
    /// when not all of them fit; without it, the order is by name.
    /// </summary>
    public static string Render(
        IReadOnlyCollection<CollectionDefinition> definitions,
        IReadOnlyCollection<RelationDefinition> relations,
        int budgetTokens,
        Func<CollectionDefinition, DateTimeOffset?>? lastChanged = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(relations);

        var ordered = definitions
            .OrderByDescending(definition => lastChanged?.Invoke(definition) ?? DateTimeOffset.MinValue)
            .ThenBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0) return "things stored: none yet";

        var text = new StringBuilder("things stored:\n");
        var described = 0;

        foreach (var definition in ordered)
        {
            var line = Line(definition) + "\n";
            if (described > 0 && Tokens.Estimate(text + line) > budgetTokens) break;
            text.Append(line);
            described++;
        }

        if (described < ordered.Count)
        {
            var rest = ordered.Skip(described).Select(static definition => definition.Name).ToList();
            var summary = $"... and {rest.Count.ToString(CultureInfo.InvariantCulture)} more";
            var named = summary + ": " + string.Join(", ", rest) + "\n";

            text.Append(Tokens.Estimate(text + named) <= budgetTokens ? named : summary + "\n");
        }

        var related = relations
            .Where(relation => ordered.Take(described).Any(definition => Names.Same(definition.Name, relation.FromCollection)))
            .OrderBy(static relation => relation.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var relation in related)
        {
            var line = $"- {relation.FromCollection}.{relation.FromColumn} refers to {relation.ToCollection}.{relation.ToColumn}"
                       + (relation.Purpose is { } purpose ? $" ({purpose})" : "") + "\n";
            if (Tokens.Estimate(text + line) > budgetTokens) break;
            text.Append(line);
        }

        return text.ToString().TrimEnd('\n');
    }
}
