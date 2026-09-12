using TokkDb.Assistant.Storage;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// SC-3 at the boundary: what each <see cref="ColumnType"/> is on disk, and how it comes back.
///
/// Every type is stored as itself. Nothing is written as text and parsed on the way out, which
/// is what the old adapter's <c>FieldMapSerializer</c> still carries a branch for: before the
/// engine had document values for Int64, Decimal, DateTime and Guid, they were written as
/// invariant strings, and a reader that forgot to parse them saw a collection of strings. The
/// engine has all of them now, so this is a mapping and not an encoding.
///
/// The one place two contract types share a document type is <see cref="ColumnType.Date"/> and
/// <see cref="ColumnType.Timestamp"/>, which are both <see cref="ValueTypeEnum.DateTime"/>
/// because the engine has no date-without-a-time. That is not a value being interpreted on the
/// way out: which of the two a column holds is in the column's declaration, not guessed from the
/// value, and a date written as midnight comes back as the same date. The engine's own
/// <c>DateTimeDocumentValue</c> stores through <c>DateTime.ToBinary</c>, so the Kind survives
/// and a timestamp is still UTC when it is read.
/// </summary>
internal static class EngineValues
{
    public static ValueTypeEnum TypeOf(ColumnType type) => type switch
    {
        ColumnType.Text => ValueTypeEnum.String,
        ColumnType.Integer => ValueTypeEnum.Long,
        ColumnType.Decimal => ValueTypeEnum.Decimal,
        ColumnType.Boolean => ValueTypeEnum.Boolean,
        ColumnType.Date => ValueTypeEnum.DateTime,
        ColumnType.Timestamp => ValueTypeEnum.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type.")
    };

    /// <summary>
    /// The contract type a stored column is, given what the engine says the column holds. Only
    /// used when reading a definition back, and only for the types that are not one to one.
    /// </summary>
    public static ColumnType ColumnTypeOf(ValueTypeEnum type, bool dateOnly) => type switch
    {
        ValueTypeEnum.String => ColumnType.Text,
        ValueTypeEnum.Long => ColumnType.Integer,
        ValueTypeEnum.Int => ColumnType.Integer,
        ValueTypeEnum.Decimal => ColumnType.Decimal,
        ValueTypeEnum.Boolean => ColumnType.Boolean,
        ValueTypeEnum.DateTime => dateOnly ? ColumnType.Date : ColumnType.Timestamp,
        _ => throw new NotSupportedException($"A column of '{type}' is not one this contract has a type for.")
    };

    /// <summary>A value of a column, as the engine stores it. Absence never reaches here.</summary>
    public static IDocumentValue ToDocument(ColumnType type, object? value) => value switch
    {
        null => new NullDocumentValue(),
        string text when type is ColumnType.Text => new StringDocumentValue(text),
        long number when type is ColumnType.Integer => new LongDocumentValue(number),
        decimal number when type is ColumnType.Decimal => new DecimalDocumentValue(number),
        bool flag when type is ColumnType.Boolean => new BooleanDocumentValue(flag),

        // Midnight, and no time zone, because a date has neither. Reading takes the date back
        // off it and the time of day is never looked at.
        DateOnly date when type is ColumnType.Date =>
            new DateTimeDocumentValue(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified)),

        DateTime moment when type is ColumnType.Timestamp => new DateTimeDocumentValue(moment),

        _ => throw new InvalidOperationException(
            $"A {type} column was handed {value.GetType().Name}, which the contract should have refused first.")
    };

    /// <summary>A stored value, as the column's type says to read it.</summary>
    public static object? FromDocument(ColumnType type, IDocumentValue value)
    {
        if (value.Type == ValueTypeEnum.Null) return null;

        return type switch
        {
            ColumnType.Text => ((StringDocumentValue)value).Value,
            ColumnType.Integer => value is IntDocumentValue small
                ? small.Value
                : ((LongDocumentValue)value).Value,
            ColumnType.Decimal => ((DecimalDocumentValue)value).Value,
            ColumnType.Boolean => ((BooleanDocumentValue)value).Value,
            ColumnType.Date => DateOnly.FromDateTime(((DateTimeDocumentValue)value).Value),
            // A timestamp column holds UTC, so what it reads is UTC. Every write stores one, so
            // this changes nothing for a value written as a timestamp. It is what a value
            // written when the column was a date - midnight, with no zone - becomes when the
            // column is retyped, and it is the same answer the in-memory conversion gives.
            ColumnType.Timestamp => DateTime.SpecifyKind(
                ((DateTimeDocumentValue)value).Value switch
                {
                    { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
                    var other => other
                },
                DateTimeKind.Utc),
            _ => throw new NotSupportedException($"A {type} column has no value to read.")
        };
    }
}
