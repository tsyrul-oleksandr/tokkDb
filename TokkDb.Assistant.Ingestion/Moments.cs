using System.Globalization;

namespace TokkDb.Assistant.Ingestion;

/// <summary>Which of the two leading numbers in a written date is the day.</summary>
internal enum DateOrder
{
    /// <summary>Nothing in the value says. 03/04/2026 is the third of April or the fourth of March.</summary>
    Unknown = 0,
    DayFirst,
    MonthFirst
}

/// <summary>What one piece of text turned out to be, as a date or a moment.</summary>
internal readonly record struct MomentReading(
    bool IsMoment,
    bool HasTime,
    DateTime Value,
    DateOrder Order,
    bool Ambiguous)
{
    public static readonly MomentReading NotAMoment = new(false, false, default, DateOrder.Unknown, false);
}

/// <summary>
/// Reading a date written by somebody else.
///
/// The same problem as <see cref="Numbers"/> and the same answer: settle what the value settles,
/// and leave the one thing it cannot to the column. <c>2026-07-20</c> says what it is.
/// <c>20/07/2026</c> says what it is, because there is no twentieth month. <c>03/04/2026</c>
/// says nothing, and the only thing that can tell is another value in the same column with a
/// number above twelve in it.
///
/// A column that never resolves - every value between the first and the twelfth - is read
/// day-first, because the form is far commoner outside one country and because the alternative
/// is refusing to read it at all. That guess is recorded, so the assistant can say which way it
/// read them.
/// </summary>
internal static class Moments
{
    /// <summary>
    /// Shapes that say what they are. ISO first, because it is the one a machine wrote.
    /// </summary>
    private static readonly string[] Unambiguous =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "yyyy.MM.dd",
        "yyyyMMdd"
    ];

    private static readonly string[] UnambiguousWithTime =
    [
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm"
    ];

    private static readonly char[] Separators = ['/', '.', '-'];

    public static MomentReading Read(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 6) return MomentReading.NotAMoment;

        // A machine-written moment with a zone on it. Kept as the instant it names.
        if (DateTimeOffset.TryParseExact(
                trimmed,
                ["yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ssK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var offset))
        {
            return new MomentReading(true, HasTime: true, offset.UtcDateTime, DateOrder.Unknown, false);
        }

        if (DateTime.TryParseExact(
                trimmed, UnambiguousWithTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
        {
            return new MomentReading(true, HasTime: true, moment, DateOrder.Unknown, false);
        }

        if (DateTime.TryParseExact(
                trimmed, Unambiguous, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return new MomentReading(true, HasTime: false, day, DateOrder.Unknown, false);
        }

        return ReadTwoLeadingNumbers(trimmed);
    }

    /// <summary>The reading again, once the column has said which way round its dates are.</summary>
    public static MomentReading Settle(MomentReading reading, string text, DateOrder columnOrder)
    {
        if (!reading.IsMoment || !reading.Ambiguous) return reading;

        var order = columnOrder is DateOrder.Unknown ? DateOrder.DayFirst : columnOrder;
        var settled = ReadTwoLeadingNumbers(text.Trim(), order);

        return settled.IsMoment ? settled with { Ambiguous = false } : MomentReading.NotAMoment;
    }

    /// <summary>What a value is when nothing in its column settles the order. Day first.</summary>
    public static MomentReading SettleUnaided(MomentReading reading, string text) =>
        Settle(reading, text, DateOrder.DayFirst);

    private static MomentReading ReadTwoLeadingNumbers(string text, DateOrder assume = DateOrder.Unknown)
    {
        var datePart = text;
        var timePart = "";

        var space = text.IndexOf(' ');
        if (space > 0)
        {
            datePart = text[..space];
            timePart = text[(space + 1)..].Trim();
        }

        var separator = Separators.FirstOrDefault(datePart.Contains);
        if (separator == default) return MomentReading.NotAMoment;

        var parts = datePart.Split(separator);
        if (parts.Length != 3) return MomentReading.NotAMoment;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return MomentReading.NotAMoment;
        }

        if (parts[2].Length == 2) year += year < 70 ? 2000 : 1900;
        if (year is < 1 or > 9999) return MomentReading.NotAMoment;

        // Which of the two is the day, and whether the value says so.
        var order = assume;
        var ambiguous = false;

        if (order is DateOrder.Unknown)
        {
            if (first > 12 && second <= 12) order = DateOrder.DayFirst;
            else if (second > 12 && first <= 12) order = DateOrder.MonthFirst;
            else
            {
                order = DateOrder.DayFirst;
                ambiguous = first <= 12 && second <= 12;
            }
        }

        var dayOfMonth = order is DateOrder.DayFirst ? first : second;
        var month = order is DateOrder.DayFirst ? second : first;

        if (month is < 1 or > 12) return MomentReading.NotAMoment;
        if (dayOfMonth < 1 || dayOfMonth > DateTime.DaysInMonth(year, month)) return MomentReading.NotAMoment;

        var value = new DateTime(year, month, dayOfMonth, 0, 0, 0, DateTimeKind.Unspecified);
        var hasTime = false;

        if (timePart.Length > 0)
        {
            if (!TimeOnly.TryParseExact(
                    timePart, ["HH:mm:ss", "HH:mm", "H:mm", "H:mm:ss"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
            {
                return MomentReading.NotAMoment;
            }

            value = value.Add(time.ToTimeSpan());
            hasTime = true;
        }

        return new MomentReading(true, hasTime, value, order, ambiguous);
    }
}
