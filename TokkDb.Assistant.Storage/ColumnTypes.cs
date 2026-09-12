using System.Globalization;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// What each <see cref="ColumnType"/> accepts on the way in, and the one CLR type it hands back.
///
/// This is the whole of SC-3 in one place. A value is coerced at most by a conversion that
/// cannot lose anything and cannot fail: an <see cref="int"/> becomes a <see cref="long"/>, a
/// local <see cref="DateTime"/> becomes the same instant in UTC. Anything that would need
/// parsing, rounding, guessing a time zone, or choosing a culture is refused instead, because a
/// storage that parses on the way in has to parse on the way out, and then the value it holds is
/// not the value it was given.
///
/// Both implementations of <see cref="IStorage"/> judge values here, so that neither can be
/// lenient where the other is strict.
/// </summary>
internal static class ColumnTypes
{
    /// <summary>
    /// Converts <paramref name="value"/> to the CLR type <paramref name="type"/> stores, or
    /// returns false if it is not a value of that type. Null passes through: an absent value has
    /// no type to be wrong about, and <see cref="ColumnDefinition.Required"/> is what decides
    /// whether absence is allowed.
    /// </summary>
    public static bool TryCanonicalise(ColumnType type, object? value, out object? canonical)
    {
        if (value is null)
        {
            canonical = null;
            return true;
        }

        switch (type)
        {
            case ColumnType.Text:
                canonical = value as string;
                return value is string;

            case ColumnType.Integer:
                return TryWidenToInt64(value, out canonical);

            case ColumnType.Decimal:
                if (value is decimal exact)
                {
                    canonical = exact;
                    return true;
                }

                if (TryWidenToInt64(value, out var whole))
                {
                    canonical = (decimal)(long)whole!;
                    return true;
                }

                canonical = null;
                return false;

            case ColumnType.Boolean:
                canonical = value as bool?;
                return value is bool;

            case ColumnType.Date:
                canonical = value as DateOnly?;
                return value is DateOnly;

            case ColumnType.Timestamp:
                return TryToUtc(value, out canonical);

            default:
                canonical = null;
                return false;
        }
    }

    /// <summary>The CLR type a column of this type hands back, for error messages and for tests.</summary>
    public static Type ClrType(ColumnType type) => type switch
    {
        ColumnType.Text => typeof(string),
        ColumnType.Integer => typeof(long),
        ColumnType.Decimal => typeof(decimal),
        ColumnType.Boolean => typeof(bool),
        ColumnType.Date => typeof(DateOnly),
        ColumnType.Timestamp => typeof(DateTime),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type.")
    };

    private static bool TryWidenToInt64(object value, out object? canonical)
    {
        // Every signed and unsigned integer that fits in a long, which is all of them except
        // ulong's upper half. A ulong that large is refused rather than wrapped.
        switch (value)
        {
            case long l: canonical = l; return true;
            case int i: canonical = (long)i; return true;
            case short s: canonical = (long)s; return true;
            case sbyte sb: canonical = (long)sb; return true;
            case byte b: canonical = (long)b; return true;
            case ushort us: canonical = (long)us; return true;
            case uint ui: canonical = (long)ui; return true;
            case ulong ul when ul <= long.MaxValue: canonical = (long)ul; return true;
            default: canonical = null; return false;
        }
    }

    private static bool TryToUtc(object value, out object? canonical)
    {
        switch (value)
        {
            case DateTimeOffset offset:
                canonical = offset.UtcDateTime;
                return true;

            case DateTime { Kind: DateTimeKind.Utc } utc:
                canonical = utc;
                return true;

            case DateTime { Kind: DateTimeKind.Local } local:
                canonical = local.ToUniversalTime();
                return true;

            // Unspecified is refused. The machine's time zone is not part of the value, and
            // assuming it would make the same text mean two moments on two machines.
            default:
                canonical = null;
                return false;
        }
    }

    /// <summary>The CLR type name to put in an error, without leaking a namespace at the user.</summary>
    public static string Describe(object? value) => value switch
    {
        null => "nothing",
        string => "text",
        long or int or short or sbyte or byte or ushort or uint or ulong => "a whole number",
        decimal => "a decimal number",
        double or float => "a binary floating point number",
        bool => "true or false",
        DateOnly => "a date",
        DateTime { Kind: DateTimeKind.Unspecified } => "a date and time with no time zone",
        DateTime => "a date and time",
        DateTimeOffset => "a date and time with an offset",
        _ => value.GetType().Name
    };

    /// <summary>How a value reads in an error message. Invariant culture, so errors do not move with the machine.</summary>
    public static string Render(object? value) => value switch
    {
        null => "nothing",
        string text => $"\"{text}\"",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "nothing"
    };
}
