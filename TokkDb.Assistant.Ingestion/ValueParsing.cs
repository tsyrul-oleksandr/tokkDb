namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// One piece of text read as one kind of value, by the same readers a column is profiled with.
///
/// The extraction step hands back values as text with the kind the model thinks they are - a
/// number, a date - and something has to turn <c>12 000</c> into twelve thousand and
/// <c>14 March</c> into a day the same way a spreadsheet cell would be turned. Exposing the
/// readers keeps that one rule: a value read out of prose and a value read out of a file mean
/// the same thing, and disagree about nothing.
///
/// A value that cannot be read as the kind asked for is reported as such rather than guessed
/// at: the caller decides whether to keep it as text (IN-1b's widening) or to say so.
/// </summary>
public static class ValueParsing
{
    /// <summary>
    /// Reads <paramref name="text"/> as a value of <paramref name="kind"/>. Returns false when
    /// it is not one, and true with <paramref name="value"/> null when the text is blank.
    /// </summary>
    public static bool TryRead(ValueKind kind, string? text, out object? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text)) return true;

        var trimmed = text.Trim();

        switch (kind)
        {
            case ValueKind.Text:
                value = trimmed;
                return true;

            case ValueKind.Boolean:
                value = Booleans.Read(trimmed);
                return value is not null;

            case ValueKind.Integer:
            {
                var number = Numbers.SettleUnaided(trimmed, Numbers.Read(trimmed));
                if (!number.IsNumber || !number.IsWhole) return false;
                value = (long)number.Value;
                return true;
            }

            case ValueKind.Decimal:
            {
                var number = Numbers.SettleUnaided(trimmed, Numbers.Read(trimmed));
                if (!number.IsNumber) return false;
                value = number.Value;
                return true;
            }

            case ValueKind.Date:
            {
                var moment = Moments.SettleUnaided(Moments.Read(trimmed), trimmed);
                if (!moment.IsMoment) return false;
                value = DateOnly.FromDateTime(moment.Value);
                return true;
            }

            case ValueKind.Timestamp:
            {
                var moment = Moments.SettleUnaided(Moments.Read(trimmed), trimmed);
                if (!moment.IsMoment) return false;
                value = DateTime.SpecifyKind(moment.Value, DateTimeKind.Utc);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// The narrowest kind that reads <paramref name="text"/>: a whole number before a number, a
    /// day before a moment, true or false before text, and text for everything - which is how
    /// a column is inferred, applied to one value.
    /// </summary>
    public static ValueKind KindOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ValueKind.Text;

        var trimmed = text.Trim();

        if (Booleans.Read(trimmed) is not null) return ValueKind.Boolean;

        var number = Numbers.SettleUnaided(trimmed, Numbers.Read(trimmed));
        if (number.IsNumber) return number.IsWhole ? ValueKind.Integer : ValueKind.Decimal;

        var moment = Moments.SettleUnaided(Moments.Read(trimmed), trimmed);
        if (moment.IsMoment) return moment.HasTime ? ValueKind.Timestamp : ValueKind.Date;

        return ValueKind.Text;
    }
}
