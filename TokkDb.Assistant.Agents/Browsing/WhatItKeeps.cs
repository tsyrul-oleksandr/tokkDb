using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Browsing;

/// <summary>One field of a thing, in the words a person would use (BR-6).</summary>
/// <param name="Name">What it is called, as the person sees it.</param>
/// <param name="Kind">What kind of value it holds: "some words", "an amount", "a day".</param>
/// <param name="Notes">"always needed", "no two the same", "often left empty (12 of 47)".</param>
public sealed record FieldInWords(string Name, string Kind, IReadOnlyList<string> Notes)
{
    public string Sentence => Notes.Count == 0 ? $"{Name} keeps {Kind}." : $"{Name} keeps {Kind}; {string.Join(", ", Notes)}.";
}

/// <summary>
/// What a thing keeps, on demand and in plain words (BR-6, UI-2's third tier): each field, what
/// kind of value it holds, whether it must be unique, whether it is required - and none of the
/// words column, type, index, schema, constraint or nullable, because a person looking at their
/// own things is not being asked to learn a vocabulary.
/// </summary>
public static class WhatItKeeps
{
    /// <summary>The kind of value, as a person would say it.</summary>
    public static string Kind(ColumnType type) => type switch
    {
        ColumnType.Text => "some words",
        ColumnType.Integer => "a whole number",
        ColumnType.Decimal => "an amount",
        ColumnType.Boolean => "yes or no",
        ColumnType.Date => "a day",
        ColumnType.Timestamp => "a day and a time",
        _ => "something"
    };

    /// <summary>A name as a person reads it: underscores as spaces.</summary>
    public static string Plain(string name) => name.Replace('_', ' ');

    /// <summary>
    /// The fields of a thing, described. <paramref name="records"/> is what the "often left empty"
    /// note is counted over; the fingerprint the importer keeps is left out, because it is the
    /// storage's bookkeeping and not something the person keeps.
    /// </summary>
    public static IReadOnlyList<FieldInWords> Describe(CollectionDefinition definition, IReadOnlyCollection<StorageRecord>? records = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var fields = new List<FieldInWords>();
        var total = records?.Count ?? 0;

        foreach (var column in definition.Columns)
        {
            if (Names.Same(column.Name, Fingerprints.ColumnName)) continue;

            var notes = new List<string>();
            if (column.Required) notes.Add("always needed");
            if (column.Unique) notes.Add("no two the same");
            if (column.ReadOnly) notes.Add("set once and kept");

            if (records is not null && total > 0)
            {
                var empty = records.Count(record => record[column.Name] is null);
                if (empty > 0 && empty * 2 >= total) notes.Add($"often left empty ({empty} of {total})");
                else if (empty > 0) notes.Add($"empty in {empty} of {total}");

                var pending = records.Count(record => record.NeedsAttention.Contains(column.Name));
                if (pending > 0) notes.Add($"{pending} waiting to be made sense of");
            }

            if (column.DefaultValue is { } fallback) notes.Add($"starts as {Orchestration.Values.Show(fallback)}");

            fields.Add(new FieldInWords(Plain(column.Name), Kind(column.Type), notes));
        }

        return fields;
    }

    /// <summary>What a relation means, as a heading a person would write (BR-7).</summary>
    public static string Heading(RelationDefinition relation, bool fromThisThing)
    {
        ArgumentNullException.ThrowIfNull(relation);

        if (relation.Purpose is { } purpose)
        {
            return fromThisThing ? Capitalise(purpose) : Capitalise($"{Plain(relation.FromCollection)} that point here as {purpose}");
        }

        return fromThisThing
            ? $"The {Plain(relation.ToCollection)} this belongs to"
            : $"The {Plain(relation.FromCollection)} that belong to this";
    }

    /// <summary>The words the first tier must not contain (UI-2, BR-6), checked by a test over every string here.</summary>
    public static readonly IReadOnlyList<string> ForbiddenWords = ["column", "type", "index", "schema", "constraint", "nullable", "collection", "query", "table", "record"];

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
