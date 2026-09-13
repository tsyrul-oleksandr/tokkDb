using TokkDb.Assistant.Storage;
using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Values;

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
///
/// <b>A value of the wrong type is read, not dropped</b> (SC-6b). A column that was retyped has
/// records holding the old type; each one is converted where it converts and kept as it is where
/// it does not, and either way it is wrapped in a <see cref="StoredAs"/> so that the storage
/// above can tell what happened. Writing goes the same way round: a value is written as what it
/// is rather than as what the column says it should be, which is what lets a record with one
/// unreadable cell be updated at all.
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

            var actual = held is StoredAs stored ? stored.Value : held;

            // Written as what it is. Normally that is the column's type, because the contract
            // refused everything else on the way in; the exception is a value the column was
            // retyped out from under (SC-6b), which is handed back to a write unchanged and has
            // to go back down as what it was rather than be forced into a type it is not.
            written[column] = actual is not null
                              && ColumnTypes.TryRecordedType(actual, out var recorded)
                              && recorded != columnType
                ? EngineValues.ToDocument(recorded, actual)
                : EngineValues.ToDocument(columnType, actual);
        }

        return SerializeObjectValue(written);
    }

    protected override object DeserializeObjectValue(Dictionary<string, IDocumentValue> values, Type type)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (column, stored) in values)
        {
            // A column that has been removed from the definition since the record was written
            // is not part of the record any more: the engine's own migration log has already
            // decided that, and a field surviving it is one this definition does not describe.
            if (!_types.TryGetValue(column, out var columnType)) continue;

            fields[column] = Read(columnType, stored);
        }

        return fields;
    }

    /// <summary>
    /// One stored value, as the column's type says to read it - or as near to that as the value
    /// allows.
    ///
    /// The retype a layer up is lazy in exactly the way the engine's own is: nothing was
    /// rewritten when the column changed, so this is where a value written under the old type
    /// becomes one of the new one. What differs is the failure: the engine turns a value it
    /// cannot read into null, and SC-6b says to keep it. See <c>ColumnConversion</c> for why the
    /// step was taken out of the engine's hands rather than configured.
    /// </summary>
    private static object? Read(ColumnType columnType, IDocumentValue stored)
    {
        if (stored.Type == ValueTypeEnum.Null) return null;

        // The types the column declares and the document holds agree for everything but a date,
        // which shares its document type with a timestamp - so the column's own answer is right
        // whenever the document type matches.
        if (stored.Type == EngineValues.TypeOf(columnType))
        {
            return EngineValues.FromDocument(columnType, stored);
        }

        var recorded = EngineValues.ColumnTypeOf(stored.Type, dateOnly: false);
        var value = EngineValues.FromDocument(recorded, stored);

        return ColumnConversion.TryRetype(recorded, columnType, value, out var converted)
            ? new StoredAs(value, recorded, converted, CanConvert: true)
            : new StoredAs(value, recorded, null, CanConvert: false);
    }
}
