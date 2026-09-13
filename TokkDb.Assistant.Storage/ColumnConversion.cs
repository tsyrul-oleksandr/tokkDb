using System.Globalization;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// What a stored value becomes when its column is retyped, and whether anything can be lost.
///
/// SC-6 says a retyped column serves records written on both sides of the change, which means
/// something has to be decided about a value written under the old type. SC-6a gives the rule
/// and SC-6b gives the exception:
///
/// <list type="bullet">
/// <item>a value already of the new type is left exactly as it is, so a decimal's scale and a
/// timestamp's zone do not go through text and back;</item>
/// <item>otherwise the value goes through its invariant text and is read back as the new type,
/// so a number retyped to text keeps its digits and text retyped to a number keeps its
/// value;</item>
/// <item><b>a value that cannot be read as the new type is kept as it is</b> and the column is
/// marked as needing attention (SC-6b). It is not dropped, and the retype is not refused.</item>
/// </list>
///
/// <b>Why this is no longer the engine's rule, which it used to be.</b> The engine's
/// <c>ValueMigration</c> turns a value it cannot read into null, and says so in its own comment:
/// a record whose old value has no meaning under the new type has no value for that column. That
/// is a defensible answer for a database and the wrong one here, because SC-6b forbids exactly
/// it - the value is the only copy, a person has to be able to see what it was to decide what it
/// should be, and a column full of quietly emptied cells is the failure this document works
/// hardest to prevent. So the one migration step whose semantics differ is performed a layer up,
/// in the same lazy shape: converted on read, converged on demand, and never rewritten by the
/// change itself. Rename and remove are still the engine's, because on those the two agree.
///
/// The text formats below are still the engine's formats - <c>True</c>/<c>False</c>, invariant
/// numbers, round-trip <c>"O"</c> for a moment - so that a value written by one and read by the
/// other means the same thing.
/// </summary>
internal static class ColumnConversion
{
    /// <summary>
    /// Whether every value of <paramref name="from"/> is a value of <paramref name="to"/>, so
    /// that the change can be made with no evidence and no question (SC-6a).
    ///
    /// One rule with a short list of exceptions, rather than a matrix nobody maintains: widening
    /// is lossless. Anything is text, a whole number is a number, and a date is a moment at
    /// midnight. Everything else is a direction in which something can stop making sense, and is
    /// a change under D-14 whose evidence is the count of values that will not convert.
    /// </summary>
    public static bool IsLossless(ColumnType from, ColumnType to) =>
        from == to
        || to is ColumnType.Text
        || (from is ColumnType.Integer && to is ColumnType.Decimal)
        || (from is ColumnType.Date && to is ColumnType.Timestamp);

    /// <summary>
    /// Reads a value recorded as one type as though it were of another, saying whether it could
    /// be. False leaves <paramref name="converted"/> holding nothing, and the caller keeps what
    /// it had - see SC-6b, and <see cref="StorageRecord.NeedsAttention"/>.
    /// </summary>
    public static bool TryRetype(ColumnType from, ColumnType to, object? value, out object? converted)
    {
        // Nothing is nothing under every type. A record with no value for a column had none
        // before the retype either.
        if (value is null)
        {
            converted = null;
            return true;
        }

        if (from == to)
        {
            converted = value;
            return true;
        }

        // Date and Timestamp are one type underneath - the engine has no date without a time -
        // so converting between them is a reinterpretation and not a reparse. Midnight on the
        // day, and the day the moment fell on.
        if (from is ColumnType.Date && to is ColumnType.Timestamp && value is DateOnly day)
        {
            converted = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return true;
        }

        if (from is ColumnType.Timestamp && to is ColumnType.Date && value is DateTime moment)
        {
            converted = DateOnly.FromDateTime(moment);
            return true;
        }

        var text = AsText(from, value);
        if (text is null)
        {
            converted = null;
            return false;
        }

        converted = Parse(to, text);
        return converted is not null;
    }

    /// <summary>
    /// <see cref="TryRetype"/> for a caller that has already decided what an unreadable value
    /// means to it. Used for a column's declared default, which is not a stored value and has
    /// nowhere to be flagged: one that has no meaning under the new type leaves the column
    /// without a default.
    /// </summary>
    public static object? Retype(ColumnType from, ColumnType to, object? value) =>
        TryRetype(from, to, value, out var converted) ? converted : null;

    /// <summary>The invariant text a value round trips through, in the engine's spelling of it.</summary>
    private static string? AsText(ColumnType type, object value) => type switch
    {
        ColumnType.Text => value as string,
        ColumnType.Integer => value is long whole ? whole.ToString(CultureInfo.InvariantCulture) : null,
        ColumnType.Decimal => value is decimal exact ? exact.ToString(CultureInfo.InvariantCulture) : null,
        ColumnType.Boolean => value is bool flag ? (flag ? "True" : "False") : null,
        ColumnType.Date => value is DateOnly day
            ? day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture)
            : null,
        ColumnType.Timestamp => value is DateTime moment
            ? moment.ToString("O", CultureInfo.InvariantCulture)
            : null,
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
