using System.Globalization;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// What a stored value becomes when its column is retyped.
///
/// SC-6 says a retyped column serves records written on both sides of the change, which means
/// something has to be decided about a value written under the old type. The rule is the
/// engine's, taken deliberately rather than invented:
///
/// <list type="bullet">
/// <item>the value goes through its invariant text and is read back as the new type, so a
/// number retyped to text keeps its digits and text retyped to a number keeps its value;</item>
/// <item>a value that cannot be read as the new type <b>becomes nothing</b>, rather than
/// failing the retype. A retype is a statement about what the column means from now on, and a
/// record whose old value has no meaning under it has no value for that column - which is the
/// same situation as a record written before the column existed;</item>
/// <item>a value that is already the new type is left exactly as it is, so a decimal's scale
/// and a timestamp's zone do not go through text and back.</item>
/// </list>
///
/// <b>This mirrors the engine's <c>ValueMigration</c> on purpose, and the two have to agree.</b>
/// The engine-backed storage does not call this - it records a migration step and the engine
/// replays it lazily on read, which is what SC-6 asks for - while the in-memory one applies this
/// at the moment of the retype. If the two rules differed, the same retype would mean two things
/// and the contract suite is what would find out. The formats below are therefore the engine's
/// formats: <c>True</c>/<c>False</c>, invariant numbers, and round-trip <c>"O"</c> for a moment.
/// </summary>
internal static class ColumnConversion
{
    public static object? Retype(ColumnType from, ColumnType to, object? value)
    {
        if (value is null) return null;
        if (from == to) return value;

        // Date and Timestamp are one type underneath - the engine has no date without a time -
        // so converting between them is a reinterpretation and not a reparse. Midnight on the
        // day, and the day the moment fell on.
        if (from is ColumnType.Date && to is ColumnType.Timestamp)
        {
            return ((DateOnly)value).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        }

        if (from is ColumnType.Timestamp && to is ColumnType.Date)
        {
            return DateOnly.FromDateTime((DateTime)value);
        }

        var text = AsText(from, value);
        return text is null ? null : Parse(to, text);
    }

    /// <summary>The invariant text a value round trips through, in the engine's spelling of it.</summary>
    private static string? AsText(ColumnType type, object value) => type switch
    {
        ColumnType.Text => (string)value,
        ColumnType.Integer => ((long)value).ToString(CultureInfo.InvariantCulture),
        ColumnType.Decimal => ((decimal)value).ToString(CultureInfo.InvariantCulture),
        ColumnType.Boolean => (bool)value ? "True" : "False",
        ColumnType.Date => ((DateOnly)value)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified)
            .ToString("O", CultureInfo.InvariantCulture),
        ColumnType.Timestamp => ((DateTime)value).ToString("O", CultureInfo.InvariantCulture),
        _ => null
    };

    private static object? Parse(ColumnType type, string text) => type switch
    {
        ColumnType.Text => text,
        ColumnType.Integer => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
            ? whole
            : null,
        ColumnType.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var exact)
            ? exact
            : null,
        ColumnType.Boolean => bool.TryParse(text, out var flag) ? flag : null,
        ColumnType.Date => DateTime.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var day)
            ? DateOnly.FromDateTime(day)
            : null,
        // A timestamp column holds UTC. Text that carried a zone is converted; text that
        // carried none is taken as UTC rather than as local, because the machine's time zone is
        // not part of a value that was written somewhere else - and because the engine's own
        // reader, which parses the same text on the other implementation, has no time zone to
        // apply either. Reading it as local would make the same database say two things on two
        // machines.
        ColumnType.Timestamp => DateTime.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.Kind switch
            {
                DateTimeKind.Utc => moment,
                DateTimeKind.Local => moment.ToUniversalTime(),
                _ => DateTime.SpecifyKind(moment, DateTimeKind.Utc)
            }
            : null,
        _ => null
    };
}
