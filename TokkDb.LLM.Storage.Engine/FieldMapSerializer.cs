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
    /// The engine has document values for String, Boolean, Int and Ulid only — Int64,
    /// Decimal, DateTime and Guid have a <c>ValueTypeEnum</c> member but no
    /// <c>IDocumentValue</c> behind it. Those four are written as invariant text so the
    /// skeleton round-trips; they need real value types before an index can order them (D-3).
    /// </summary>
    private static IDocumentValue ToDocumentValue(object? value) => value switch
    {
        null => new NullDocumentValue(),
        string text => new StringDocumentValue { Value = text },
        bool flag => new BooleanDocumentValue { Value = flag },
        int number => new IntDocumentValue { Value = number },
        long number => new StringDocumentValue { Value = number.ToString(CultureInfo.InvariantCulture) },
        decimal number => new StringDocumentValue { Value = number.ToString(CultureInfo.InvariantCulture) },
        DateTime moment => new StringDocumentValue { Value = moment.ToString("O", CultureInfo.InvariantCulture) },
        Guid id => new StringDocumentValue { Value = id.ToString("D") },
        _ => throw new NotSupportedException(
            $"Value of type '{value.GetType().Name}' has no document representation in the walking skeleton.")
    };

    private object? FromDocumentValue(string fieldName, IDocumentValue value)
    {
        if (value.Type == ValueTypeEnum.Null)
        {
            return null;
        }

        // The stored form of the four text-encoded types is a string, so the column
        // definition is what says which of them it is.
        if (value is StringDocumentValue text && _columnTypes.TryGetValue(fieldName, out var columnType))
        {
            return columnType switch
            {
                ColumnType.Int64 => long.Parse(text.Value, CultureInfo.InvariantCulture),
                ColumnType.Decimal => decimal.Parse(text.Value, CultureInfo.InvariantCulture),
                ColumnType.DateTime => DateTime.Parse(
                    text.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ColumnType.Guid => Guid.Parse(text.Value),
                _ => text.Value
            };
        }

        return value switch
        {
            StringDocumentValue stringValue => stringValue.Value,
            BooleanDocumentValue booleanValue => booleanValue.Value,
            IntDocumentValue intValue => intValue.Value,
            UlidDocumentValue ulidValue => ulidValue.Value,
            _ => throw new NotSupportedException(
                $"Document value '{value.Type}' is not read by the walking skeleton.")
        };
    }
}
