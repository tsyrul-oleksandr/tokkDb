using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Browsing;

/// <summary>One field of a record as the detail shows it: shown empty rather than omitted (BR-5).</summary>
public sealed record ShownField(string Name, string Kind, string Value, bool IsEmpty, bool NeedsAttention);

/// <summary>Records one step away under a heading that says what the relation means (BR-7).</summary>
public sealed record RelatedRecords(string Heading, string Thing, IReadOnlyList<(Ulid Id, string Title)> Records);

/// <summary>
/// A record opened (BR-5, BR-7): every field it has, empty ones shown as empty, the display value
/// as its title, and the related records reachable in one step under a heading a person would write.
/// </summary>
public sealed record RecordDetail(
    string Thing,
    Ulid Id,
    string Title,
    IReadOnlyList<ShownField> Fields,
    IReadOnlyList<RelatedRecords> Related)
{
    public static RecordDetail? Open(IStorage storage, string thing, Ulid id)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var definition = storage.GetCollectionDefinition(thing);
        if (definition is null) return null;

        var record = storage.GetById(thing, id);
        if (record is null) return null;

        var fields = definition.Columns
            .Where(column => !Names.Same(column.Name, Fingerprints.ColumnName))
            .Select(column => new ShownField(
                WhatItKeeps.Plain(column.Name),
                WhatItKeeps.Kind(column.Type),
                Values.Show(record[column.Name]),
                record[column.Name] is null,
                record.NeedsAttention.Contains(column.Name)))
            .ToList();

        var related = new List<RelatedRecords>();

        foreach (var relation in storage.GetRelations())
        {
            if (Names.Same(relation.FromCollection, thing) && record[relation.FromColumn] is { } key)
            {
                // What this refers to: one record on the far side, found by the unique field.
                var far = storage.GetCollectionDefinition(relation.ToCollection);
                if (far is null) continue;
                var target = storage.ExecuteQuery(new StorageQuery(relation.ToCollection, [new QueryCondition(relation.ToColumn, QueryOperator.Equals, key)], take: 1));
                related.Add(new RelatedRecords(WhatItKeeps.Heading(relation, fromThisThing: true), relation.ToCollection,
                    [.. target.Records.Select(found => (found.Id, DisplayValue.For(far, found)))]));
            }
            else if (Names.Same(relation.ToCollection, thing) && record[relation.ToColumn] is { } value)
            {
                // What refers to this: every record on the near side holding this one's value.
                var near = storage.GetCollectionDefinition(relation.FromCollection);
                if (near is null) continue;
                var referring = storage.ExecuteQuery(new StorageQuery(relation.FromCollection, [new QueryCondition(relation.FromColumn, QueryOperator.Equals, value)], take: 200));
                related.Add(new RelatedRecords(WhatItKeeps.Heading(relation, fromThisThing: false), relation.FromCollection,
                    [.. referring.Records.Select(found => (found.Id, DisplayValue.For(near, found)))]));
            }
        }

        return new RecordDetail(thing, id, DisplayValue.For(definition, record), fields, related);
    }
}
