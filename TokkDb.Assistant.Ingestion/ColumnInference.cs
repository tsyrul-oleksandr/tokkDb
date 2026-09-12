using System.Globalization;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// What a column holds, worked out from what is in it.
///
/// Two passes, and the second one is the point. The first reads every value on its own and
/// collects what the values settle between them - which character these numbers use for their
/// fractions, which way round these dates are. The second reads them again knowing that, so a
/// value that could not say for itself is read the way its neighbours were.
///
/// A column that has been read one value at a time cannot do this, and it is why a single-pass
/// parser gets a real spreadsheet wrong: <c>1,234</c> in a column whose other values are
/// <c>1.234,56</c> is one thousand two hundred and thirty-four, and in a column whose other
/// values are <c>1234.56</c> it is also one thousand two hundred and thirty-four, and in a
/// column of <c>0,5</c> and <c>1,25</c> it is one and a bit. Only the column knows.
///
/// The type chosen is always one that holds <b>every</b> value. <see cref="ColumnProfile.Majority"/>
/// is what most of the column is, and when the two differ, the values that forced the difference
/// are listed - which is IN-1's ninety-eight per cent integer column, reported as text with its
/// two per cent named rather than quietly swallowed.
/// </summary>
/// <summary>What a column worked out about the way its values are written.</summary>
internal readonly record struct ColumnConventions(DecimalMark Mark, DateOrder Order);

internal static class ColumnInference
{
    /// <summary>How many offending values are kept. Enough to show somebody; not a second copy of the column.</summary>
    public const int MaxExceptionsKept = 8;

    private const int MaxExamples = 3;

    public static ColumnProfile Infer(string name, int position, IReadOnlyList<(int LineNumber, string? Text)> cells)
    {
        var present = cells
            .Where(static cell => !string.IsNullOrWhiteSpace(cell.Text))
            .Select(static cell => (cell.LineNumber, Text: cell.Text!.Trim()))
            .ToArray();

        var blanks = cells.Count - present.Length;

        if (present.Length == 0)
        {
            // Nothing to go on. Text holds anything, including everything that might arrive later.
            return new ColumnProfile(
                name, position, ColumnType.Text, ColumnType.Text, MajorityShare: 1,
                ValueCount: 0, BlankCount: blanks, DistinctCount: 0,
                Minimum: null, Maximum: null, Examples: [], AmbiguousCount: 0, ExceptionCount: 0,
                Exceptions: []);
        }

        // First pass: read each value alone, and note what the column settles between them.
        var numbers = new NumberReading[present.Length];
        var moments = new MomentReading[present.Length];
        var mark = DecimalMark.Unknown;
        var order = DateOrder.Unknown;

        for (var i = 0; i < present.Length; i++)
        {
            numbers[i] = Numbers.Read(present[i].Text);
            moments[i] = numbers[i].IsNumber ? MomentReading.NotAMoment : Moments.Read(present[i].Text);

            if (mark is DecimalMark.Unknown && numbers[i] is { IsNumber: true, Ambiguous: false, Mark: not DecimalMark.Unknown })
            {
                mark = numbers[i].Mark;
            }

            if (order is DateOrder.Unknown && moments[i] is { IsMoment: true, Ambiguous: false, Order: not DateOrder.Unknown })
            {
                order = moments[i].Order;
            }
        }

        // Second pass: read them again, knowing what the column says.
        var kinds = new ColumnType[present.Length];
        var typed = new object?[present.Length];
        var ambiguous = 0;

        for (var i = 0; i < present.Length; i++)
        {
            var text = present[i].Text;

            if (numbers[i].Ambiguous || moments[i].Ambiguous) ambiguous++;

            if (Booleans.Read(text) is { } flag)
            {
                kinds[i] = ColumnType.Boolean;
                typed[i] = flag;
                continue;
            }

            var number = mark is DecimalMark.Unknown
                ? Numbers.SettleUnaided(text, numbers[i])
                : Numbers.Settle(numbers[i], text, mark);

            if (number.IsNumber)
            {
                kinds[i] = number.IsWhole && number.Value == decimal.Truncate(number.Value)
                    ? ColumnType.Integer
                    : ColumnType.Decimal;
                typed[i] = number.Value;
                continue;
            }

            var moment = order is DateOrder.Unknown
                ? Moments.SettleUnaided(moments[i], text)
                : Moments.Settle(moments[i], text, order);

            if (moment.IsMoment)
            {
                kinds[i] = moment.HasTime ? ColumnType.Timestamp : ColumnType.Date;
                typed[i] = moment.Value;
                continue;
            }

            kinds[i] = ColumnType.Text;
            typed[i] = text;
        }

        var majority = kinds
            .GroupBy(static kind => kind)
            .OrderByDescending(static group => group.Count())
            // A tie goes to the narrower reading. Text holds everything, so calling it the
            // majority says nothing about the column; saying most of it is whole numbers does.
            .ThenBy(static group => group.Key is ColumnType.Text ? 1 : 0)
            .ThenBy(static group => (int)group.Key)
            .First();

        var inferred = Narrowest(kinds);

        var exceptions = present
            .Select((cell, i) => (cell.LineNumber, cell.Text, Kind: kinds[i]))
            .Where(entry => entry.Kind != majority.Key)
            .ToArray();

        var distinct = present
            .Select(static cell => cell.Text)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var (minimum, maximum) = Extremes(inferred, kinds, typed, present);

        return new ColumnProfile(
            name,
            position,
            inferred,
            majority.Key,
            (double)majority.Count() / present.Length,
            present.Length,
            blanks,
            distinct.Length,
            minimum,
            maximum,
            [.. distinct.Take(MaxExamples)],
            ambiguous,
            exceptions.Length,
            [.. exceptions.Take(MaxExceptionsKept).Select(static entry => new ColumnException(entry.LineNumber, entry.Text))])
        {
            Conventions = new ColumnConventions(mark, order)
        };
    }

    /// <summary>
    /// One cell, read as the column concluded it should be. The same two steps the second pass
    /// takes, so a row turned into a record holds the values the profile described.
    /// </summary>
    public static object? Read(ColumnProfile profile, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();

        return profile.Inferred switch
        {
            ColumnType.Text => trimmed,
            ColumnType.Boolean => Booleans.Read(trimmed),
            ColumnType.Integer => AsNumber(profile, trimmed) is { } whole ? (long)whole : null,
            ColumnType.Decimal => AsNumber(profile, trimmed),
            ColumnType.Date => AsMoment(profile, trimmed) is { } day ? DateOnly.FromDateTime(day) : null,
            ColumnType.Timestamp => AsMoment(profile, trimmed) is { } moment
                ? DateTime.SpecifyKind(moment, DateTimeKind.Utc)
                : null,
            _ => null
        };
    }

    private static decimal? AsNumber(ColumnProfile profile, string text)
    {
        var read = Numbers.Read(text);

        var settled = profile.Conventions.Mark is DecimalMark.Unknown
            ? Numbers.SettleUnaided(text, read)
            : Numbers.Settle(read, text, profile.Conventions.Mark);

        return settled.IsNumber ? settled.Value : null;
    }

    private static DateTime? AsMoment(ColumnProfile profile, string text)
    {
        var read = Moments.Read(text);

        var settled = profile.Conventions.Order is DateOrder.Unknown
            ? Moments.SettleUnaided(read, text)
            : Moments.Settle(read, text, profile.Conventions.Order);

        return settled.IsMoment ? settled.Value : null;
    }

    /// <summary>
    /// The narrowest type that holds every value. A whole number fits where a decimal is
    /// expected and a date fits where a moment is; nothing else widens, so a column holding two
    /// kinds that are not one of those pairs is text - which holds everything and loses nothing.
    /// </summary>
    private static ColumnType Narrowest(IReadOnlyList<ColumnType> kinds)
    {
        var distinct = kinds.Distinct().ToArray();

        if (distinct.Length == 1) return distinct[0];

        if (distinct.All(static kind => kind is ColumnType.Integer or ColumnType.Decimal))
        {
            return ColumnType.Decimal;
        }

        if (distinct.All(static kind => kind is ColumnType.Date or ColumnType.Timestamp))
        {
            return ColumnType.Timestamp;
        }

        return ColumnType.Text;
    }

    /// <summary>
    /// The smallest and largest the column holds, rendered as text because that is what the
    /// profile is for - a person reading it and a model being shown it. Ordered by what the
    /// values are rather than by how they were written, so a column of numbers does not report
    /// its maximum as the one that starts with a nine.
    /// </summary>
    private static (string? Minimum, string? Maximum) Extremes(
        ColumnType inferred,
        IReadOnlyList<ColumnType> kinds,
        IReadOnlyList<object?> typed,
        IReadOnlyList<(int LineNumber, string Text)> present)
    {
        if (inferred is ColumnType.Text)
        {
            var ordered = present.Select(static cell => cell.Text).Order(StringComparer.Ordinal).ToArray();
            return (ordered[0], ordered[^1]);
        }

        var values = typed.Where(static value => value is not null).Cast<IComparable>().ToArray();
        if (values.Length == 0) return (null, null);

        var sorted = values.Order().ToArray();
        return (Render(sorted[0]), Render(sorted[^1]));
    }

    private static string Render(object value) => value switch
    {
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        DateTime { TimeOfDay.Ticks: 0 } day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => value.ToString() ?? ""
    };
}

/// <summary>
/// True and false as people write them in a spreadsheet.
///
/// <c>1</c> and <c>0</c> are deliberately not here. A column of ones and zeroes is a column of
/// numbers far more often than it is a column of answers, and reading it as true and false would
/// turn a count into a flag with nothing to say it had happened.
/// </summary>
internal static class Booleans
{
    private static readonly string[] True = ["true", "yes", "y"];
    private static readonly string[] False = ["false", "no", "n"];

    public static bool? Read(string text)
    {
        var trimmed = text.Trim();

        if (True.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return true;
        if (False.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return false;

        return null;
    }
}
