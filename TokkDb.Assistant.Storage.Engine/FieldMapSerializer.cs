using TokkDb.Assistant.Storage;
using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// A record as the contract sees it - a map of column name to value - written as a document.
///
/// The engine's own serializer reflects over the properties of a CLR type, and a field map has
/// none, so the two hooks it exposes for object values are replaced. This is the shape the
/// engine anticipated: <c>TokkDbConnection.Entities</c> takes a serializer precisely so that a
/// caller whose records are field maps described by a collection definition can supply one.
///
/// It carries the definition because the definition is the only place the column types are
/// recorded, and SC-3 means a value is written as its column's type and read back as it.
///
/// <b>A column with no value is not in the document at all.</b> A column present and holding
/// nothing is, as a null. The contract distinguishes the two - "never given" and "given as
/// nothing" - and a serializer that wrote both the same way would make the engine-backed
/// storage disagree with the in-memory one on the first record anybody cleared a field on.
/// </summary>
internal sealed class FieldMapSerializer : DocumentSerializer<Dictionary<string, object?>>
{
    private readonly Dictionary<string, ColumnType> _types;

    public FieldMapSerializer(CollectionDefinition definition)
    {
        _types = definition.Columns.ToDictionary(
            static column => column.Name,
            static column => column.Type,
            StringComparer.Ordinal);
    }

    protected override IDocumentValue SerializeObjectValue(object value, Type type)
    {
        var fields = (Dictionary<string, object?>)value;
        var written = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);

        foreach (var (column, held) in fields)
        {
            // A column the definition no longer has cannot be typed, so it cannot be written.
            // The contract refuses the write before this, so reaching here would be a bug.
            if (!_types.TryGetValue(column, out var columnType))
            {
                throw new InvalidOperationException(
                    $"'{column}' is not a column of this collection and should not have reached the serializer.");
            }

            written[column] = EngineValues.ToDocument(columnType, held);
        }

        return SerializeObjectValue(written);
    }

    protected override object DeserializeObjectValue(Dictionary<string, IDocumentValue> values, Type type)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (column, stored) in values)
        {
            // A column that has been removed from the definition since the record was written
            // is not part of the record any more. The engine's lazy migration is what will
            // decide this properly in 1.4; until then, dropping it is what the definition says.
            if (!_types.TryGetValue(column, out var columnType)) continue;

            fields[column] = EngineValues.FromDocument(columnType, stored);
        }

        return fields;
    }
}
