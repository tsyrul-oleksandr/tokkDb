using System.Globalization;

namespace TokkDb.LLM.Core;

public sealed class ColumnValue
{
    private ColumnValue() { }

    public string? StringValue { get; private init; }
    public bool? BooleanValue { get; private init; }
    public int? Int32Value { get; private init; }
    public long? Int64Value { get; private init; }
    public decimal? DecimalValue { get; private init; }
    public DateTime? DateTimeValue { get; private init; }
    public Guid? GuidValue { get; private init; }

    public static ColumnValue FromString(string value) => new() { StringValue = value };
    public static ColumnValue FromBoolean(bool value) => new() { BooleanValue = value };
    public static ColumnValue FromInt32(int value) => new() { Int32Value = value };
    public static ColumnValue FromInt64(long value) => new() { Int64Value = value };
    public static ColumnValue FromDecimal(decimal value) => new() { DecimalValue = value };
    public static ColumnValue FromDateTime(DateTime value) => new() { DateTimeValue = value };
    public static ColumnValue FromGuid(Guid value) => new() { GuidValue = value };

    public bool IsCompatibleWith(ColumnType columnType)
    {
        return columnType switch
        {
            ColumnType.String => StringValue is not null,
            ColumnType.Boolean => BooleanValue.HasValue,
            ColumnType.Int32 => Int32Value.HasValue,
            ColumnType.Int64 => Int64Value.HasValue,
            ColumnType.Decimal => DecimalValue.HasValue,
            ColumnType.DateTime => DateTimeValue.HasValue,
            ColumnType.Guid => GuidValue.HasValue,
            _ => false
        };
    }
}

public static class ColumnValueMapper
{
    public static object? ToStorageValue(ColumnValue? value)
    {
        if (value is null) return null;
        if (value.StringValue is not null) return value.StringValue;
        if (value.BooleanValue.HasValue) return value.BooleanValue.Value;
        if (value.Int32Value.HasValue) return value.Int32Value.Value;
        if (value.Int64Value.HasValue) return value.Int64Value.Value;
        if (value.DecimalValue.HasValue) return value.DecimalValue.Value;
        if (value.DateTimeValue.HasValue) return value.DateTimeValue.Value;
        if (value.GuidValue.HasValue) return value.GuidValue.Value;
        return null;
    }

    public static ColumnValue? ParseFromString(ColumnType type, string? value)
    {
        if (value is null)
        {
            return null;
        }
        return type switch
        {
            ColumnType.String => ColumnValue.FromString(value),
            ColumnType.Boolean => bool.TryParse(value, out var b) ? ColumnValue.FromBoolean(b) : null,
            ColumnType.Int32 => int.TryParse(value, out var i) ? ColumnValue.FromInt32(i) : null,
            ColumnType.Int64 => long.TryParse(value, out var l) ? ColumnValue.FromInt64(l) : null,
            ColumnType.Decimal => decimal.TryParse(value, out var d) ? ColumnValue.FromDecimal(d) : null,
            ColumnType.DateTime => DateTime.TryParse(value, out var dt) ? ColumnValue.FromDateTime(dt) : null,
            ColumnType.Guid => Guid.TryParse(value, out var g) ? ColumnValue.FromGuid(g) : null,
            _ => null
        };
    }

    public static string? ToString(ColumnType type, ColumnValue? value)
    {
        if (value is null)
        {
            return null;
        }
        return type switch
        {
            ColumnType.String => value.StringValue,
            ColumnType.Boolean => value.BooleanValue.ToString(),
            ColumnType.Int32 => value.Int32Value?.ToString(CultureInfo.InvariantCulture),
            ColumnType.Int64 => value.Int64Value?.ToString(CultureInfo.InvariantCulture),
            ColumnType.Decimal => value.DecimalValue?.ToString(CultureInfo.InvariantCulture),
            ColumnType.DateTime => value.DateTimeValue?.ToString(CultureInfo.InvariantCulture),
            ColumnType.Guid => value.GuidValue?.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    public static ColumnValue? FromStorageValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => ColumnValue.FromString(s),
            bool b => ColumnValue.FromBoolean(b),
            int i => ColumnValue.FromInt32(i),
            long l => ColumnValue.FromInt64(l),
            decimal d => ColumnValue.FromDecimal(d),
            System.DateTime dt => ColumnValue.FromDateTime(dt),
            System.Guid g => ColumnValue.FromGuid(g),
            _ => ColumnValue.FromString(value.ToString() ?? string.Empty)
        };
    }
}
