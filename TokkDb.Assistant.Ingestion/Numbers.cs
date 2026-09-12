using System.Globalization;

namespace TokkDb.Assistant.Ingestion;

/// <summary>Which character a number used to separate its fractional part, once that is known.</summary>
internal enum DecimalMark
{
    /// <summary>The value has no fractional part and no separator that settles the question.</summary>
    Unknown = 0,
    Dot,
    Comma
}

/// <summary>What one piece of text turned out to be, as a number.</summary>
internal readonly record struct NumberReading(
    bool IsNumber,
    bool IsWhole,
    decimal Value,
    DecimalMark Mark,
    bool Ambiguous)
{
    public static readonly NumberReading NotANumber = new(false, false, 0m, DecimalMark.Unknown, false);
}

/// <summary>
/// Reading a number written by somebody else.
///
/// This is the part of IN-1 that is not a formality. A spreadsheet that has been round a few
/// hands holds <c>1234.56</c>, <c>1.234,56</c>, <c>1 234,56</c> and <c>1,234.56</c> in the same
/// column, and every one of them is one thousand two hundred and thirty-four and a half. A
/// parser that picks one culture and applies it to the column gets half of them wrong, silently,
/// and the wrong ones are not obviously wrong - <c>1.234,56</c> read as invariant is not an
/// error, it is a different number.
///
/// So the convention is worked out per value rather than per column, and only the one case that
/// genuinely cannot be settled from the value alone is left to the column:
///
/// <list type="bullet">
/// <item><b>Both separators present.</b> The last one is the decimal mark and the other is a
/// thousands separator, which has to appear in groups of three or the value is not a number at
/// all. <c>1.234,56</c> and <c>1,234.56</c> are both settled here.</item>
/// <item><b>One separator, once, with something other than three digits after it.</b> It is the
/// decimal mark: <c>1234.56</c>, <c>1,5</c>, <c>0.5</c>.</item>
/// <item><b>One separator, more than once.</b> It is a thousands separator, and the value is a
/// whole number: <c>1.234.567</c>.</item>
/// <item><b>One separator, once, with exactly three digits after it.</b> This is the only
/// ambiguous case - <c>1,234</c> is either one thousand two hundred and thirty-four or one and
/// a bit - and it is left for the column to settle from its other values.</item>
/// </list>
///
/// A space, a non-breaking space and a narrow non-breaking space are all thousands separators,
/// because that is what a European spreadsheet puts there.
/// </summary>
internal static class Numbers
{
    public static NumberReading Read(string text)
    {
        if (!Prepare(text, out var body, out var negative)) return NumberReading.NotANumber;

        var dots = body.Count(static character => character == '.');
        var commas = body.Count(static character => character == ',');

        if (dots > 0 && commas > 0)
        {
            var decimalMark = body.LastIndexOf('.') > body.LastIndexOf(',') ? '.' : ',';
            return Settled(body, decimalMark, negative, ambiguous: false);
        }

        if (dots == 0 && commas == 0)
        {
            return decimal.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)
                ? new NumberReading(true, IsWhole: true, negative ? -whole : whole, DecimalMark.Unknown, false)
                : NumberReading.NotANumber;
        }

        var mark = dots > 0 ? '.' : ',';
        var count = dots > 0 ? dots : commas;

        if (count > 1)
        {
            // Repeated, so it groups. Nothing is left over to be a fraction.
            return Grouped(body, mark, negative);
        }

        var after = body.Length - body.LastIndexOf(mark) - 1;

        // Exactly three digits after a single separator is the one shape a value cannot settle
        // for itself. Read as a fraction, and reported as ambiguous so the column can overrule.
        return Settled(body, mark, negative, ambiguous: after == 3 && body.IndexOf(mark) > 0);
    }

    /// <summary>
    /// The reading again, once the column has said which character its numbers use for the
    /// fractional part. Only an ambiguous value changes its mind.
    /// </summary>
    public static NumberReading Settle(NumberReading reading, string text, DecimalMark columnMark)
    {
        if (!reading.IsNumber || !reading.Ambiguous) return reading;

        // The column uses the other character for its fractions, so this one groups digits: the
        // value is a whole number after all.
        return columnMark is not DecimalMark.Unknown && columnMark != reading.Mark
            ? Regroup(text, reading)
            : reading;
    }

    /// <summary>
    /// What a value looks like when nothing else in the column settles it. Three digits after a
    /// lone separator is a thousands group far more often than it is a fraction, which is the
    /// way a spreadsheet writes it and the way a person reads it.
    /// </summary>
    public static NumberReading SettleUnaided(string text, NumberReading reading) =>
        reading.Ambiguous ? Regroup(text, reading) : reading;

    /// <summary>
    /// The value read again with its separator grouping digits rather than starting a fraction.
    ///
    /// Read again from the text rather than multiplied by a thousand, because a decimal
    /// remembers how many places it was written with: 1.234 times a thousand is 1234.000, which
    /// is the right number and the wrong shape, and a profile that reported its maximum as
    /// "1234.000" would be describing the arithmetic rather than the column.
    /// </summary>
    private static NumberReading Regroup(string text, NumberReading reading)
    {
        if (!Prepare(text, out var body, out var negative)) return reading;

        var grouping = reading.Mark is DecimalMark.Dot ? '.' : ',';
        var grouped = Grouped(body, grouping, negative);

        return grouped.IsNumber ? grouped : reading;
    }

    /// <summary>
    /// The sign taken off and the digit-grouping spaces taken out, leaving digits, dots and
    /// commas - which is all the shapes below have to think about.
    /// </summary>
    private static bool Prepare(string text, out string body, out bool negative)
    {
        body = "";
        negative = false;

        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        var start = 0;

        if (trimmed[0] is '-' or '+' or '\u2212')
        {
            negative = trimmed[0] is '-' or '\u2212';
            start = 1;
        }

        // A space that grouped digits has to have grouped them in threes. One that did not was
        // not a thousands separator, and the value was two things with a gap between them.
        var chunks = trimmed[start..].Split([' ', '\u00a0', '\u202f', '\u2009'], StringSplitOptions.None);

        for (var i = 1; i < chunks.Length; i++)
        {
            if (chunks[i].TakeWhile(char.IsAsciiDigit).Count() != 3) return false;
        }

        if (chunks[0].Length == 0) return false;

        var kept = new System.Text.StringBuilder(trimmed.Length - start);

        foreach (var character in string.Concat(chunks))
        {
            if (!char.IsAsciiDigit(character) && character is not ('.' or ',')) return false;
            kept.Append(character);
        }

        body = kept.ToString();
        return body.Length > 0 && body.Any(char.IsAsciiDigit);
    }

    private static NumberReading Settled(string body, char decimalMark, bool negative, bool ambiguous)
    {
        var grouping = decimalMark == '.' ? ',' : '.';
        var split = body.LastIndexOf(decimalMark);
        var whole = body[..split];
        var fraction = body[(split + 1)..];

        if (fraction.Length == 0 || !fraction.All(char.IsAsciiDigit)) return NumberReading.NotANumber;
        if (!GroupsAreWholeThousands(whole, grouping)) return NumberReading.NotANumber;

        var plain = whole.Replace(grouping.ToString(), "", StringComparison.Ordinal);
        if (plain.Length == 0) plain = "0";

        return decimal.TryParse(
            $"{plain}.{fraction}", NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? new NumberReading(
                true,
                IsWhole: false,
                negative ? -value : value,
                decimalMark == '.' ? DecimalMark.Dot : DecimalMark.Comma,
                ambiguous)
            : NumberReading.NotANumber;
    }

    private static NumberReading Grouped(string body, char grouping, bool negative)
    {
        if (!GroupsAreWholeThousands(body, grouping)) return NumberReading.NotANumber;

        var plain = body.Replace(grouping.ToString(), "", StringComparison.Ordinal);

        return decimal.TryParse(plain, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? new NumberReading(true, IsWhole: true, negative ? -value : value, DecimalMark.Unknown, false)
            : NumberReading.NotANumber;
    }

    /// <summary>
    /// Whether a separator really grouped digits: at least one digit before the first, and
    /// exactly three after every one of them. <c>1,2345</c> is not a grouped number, and a parser
    /// that shrugged and read it as 12345 would be inventing a digit's worth of value.
    /// </summary>
    private static bool GroupsAreWholeThousands(string whole, char grouping)
    {
        if (whole.Length == 0) return true;

        // Nothing grouped it, so it is simply the digits before the fraction and there is no
        // rule about how many of them there are.
        if (!whole.Contains(grouping, StringComparison.Ordinal))
        {
            return whole.All(char.IsAsciiDigit);
        }

        var groups = whole.Split(grouping);
        if (groups[0].Length is 0 or > 3) return false;

        return groups.All(static group => group.Length > 0 && group.All(char.IsAsciiDigit))
               && groups.Skip(1).All(static group => group.Length == 3);
    }
}
