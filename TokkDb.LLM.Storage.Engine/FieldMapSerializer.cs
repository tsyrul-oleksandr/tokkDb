using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.LLM.Core;
using TokkDb.Values;
using System.Globalization;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// A record as IStorage sees it: a map of field name to value, described by the collection
/// definition rather than by a CLR type. The engine's own serializer reflects over the
/// properties of a class, which a field map does not have, so the two hooks it exposes for
/// object values are replaced here.
/// </summary>
public sealed class FieldMapSerializer : DocumentSerializer<Dictionary<string, object?>>
{
    private readonly IReadOnlyDictionary<string, ColumnType> _columnTypes;

    public FieldMapSerializer(CollectionDefinition definition)
    {
        _columnTypes = definition.Columns.ToDictionary(
            column => column.Name,
            column => column.Type,
            StringComparer.Ordinal);
    }

    protected override IDocumentValue SerializeObjectValue(object value, Type type)
    {
        var fields = (Dictionary<string, object?>)value;
        return SerializeObjectValue(fields.ToDictionary(
            field => field.Key,
            field => ToDocumentValue(field.Value),
            StringComparer.Ordinal));
    }

    protected override object DeserializeObjectValue(Dictionary<string, IDocumentValue> values, Type type)
    {
        return values.ToDictionary(
            field => field.Key,
            field => FromDocumentValue(field.Key, field.Value),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Every column type the application has is a document value the engine holds as itself.
    /// Int64, Decimal, DateTime and Guid used to be written as invariant text, because
    /// <c>ValueTypeEnum</c> declared them and nothing implemented them; they have their own
    /// values now, which is what lets D-3 order them and an index answer a range over them.
    /// </summary>
    private static IDocumentValue ToDocumentValue(object? value) => value switch
    {
        null => new NullDocumentValue(),
        string text => new StringDocumentValue(text),
        bool flag => new BooleanDocumentValue(flag),
        int number => new IntDocumentValue(number),
        long number => new LongDocumentValue(number),
        decimal number => new DecimalDocumentValue(number),
        DateTime moment => new DateTimeDocumentValue(moment),
        Guid id => new GuidDocumentValue(id),
        _ => throw new NotSupportedException(
            $"Value of type '{value.GetType().Name}' has no document representation.")
    };

    private object? FromDocumentValue(string fieldName, IDocumentValue value)
    {
        if (value.Type == ValueTypeEnum.Null)
        {
            return null;
        }

        // A record written before those four had a value of their own holds the invariant text
        // they used to be stored as, and the column definition is what says which of them it
        // is. Reading it costs one branch; not reading it would make an older database look
        // like a collection of strings.
        if (value is StringDocumentValue legacy && _columnTypes.TryGetValue(fieldName, out var columnType))
        {
            switch (columnType)
            {
                case ColumnType.Int64 when long.TryParse(
                    legacy.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number):
                    return number;
                case ColumnType.Decimal when decimal.TryParse(
                    legacy.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                    return number;
                case ColumnType.DateTime when DateTime.TryParse(
                    legacy.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment):
                    return moment;
                case ColumnType.Guid when Guid.TryParse(legacy.Value, out var identifier):
                    return identifier;
            }
        }

        return value switch
        {
            StringDocumentValue stringValue => stringValue.Value,
            BooleanDocumentValue booleanValue => booleanValue.Value,
            IntDocumentValue intValue => intValue.Value,
            LongDocumentValue longValue => longValue.Value,
            DecimalDocumentValue decimalValue => decimalValue.Value,
            DateTimeDocumentValue dateTimeValue => dateTimeValue.Value,
            GuidDocumentValue guidValue => guidValue.Value,
            UlidDocumentValue ulidValue => ulidValue.Value,
            _ => throw new NotSupportedException($"Document value '{value.Type}' has no field value.")
        };
    }
}
