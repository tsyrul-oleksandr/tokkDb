using System.Globalization;

namespace TokkDb.Assistant.Trace;

/// <summary>
/// How much of a change may be written down (TR-2b's second half).
///
/// <b>Two caps, not one.</b> A cap per value keeps one enormous cell out of the journal; a cap
/// per change keeps a thousand ordinary ones out. Neither alone is enough - a record of five
/// thousand short fields passes every per-value cap there is - and the second is the reason a
/// change can be declared irreversible before it runs rather than truncated after.
///
/// The numbers are stated rather than tuned. A value of a thousand characters is a paragraph,
/// which is worth keeping whole; the preview is a line, which is enough to recognise what was
/// there; and sixty-four kilobytes of text is far more than a record and far less than a file.
/// TR-7 and TR-8's retention windows are the other half of the size discipline and are settled
/// in step 9.2.
/// </summary>
public sealed record PayloadLimits(int LongestValue, int Preview, int LongestPayload)
{
    public static readonly PayloadLimits Default = new(LongestValue: 1024, Preview: 200, LongestPayload: 64 * 1024);
}

/// <summary>
/// What kind of thing a journalled value is.
///
/// <b>The trace has its own vocabulary for this, deliberately.</b> §3.1 puts this project below
/// everything, depending on nothing - not even the storage contract - and
/// <see cref="TokkDb.Assistant.Trace"/> is not the only place that happens: the ingestion project
/// has its own <c>ValueKind</c> for the same reason. The words are the storage contract's on
/// purpose, because the values come from there and a reader should not have to translate.
///
/// It is here because a journal that gave back text would be useless for the one thing the
/// journal is for. Storage refuses to parse on the way in (SC-3), so restoring a deleted record
/// from a row of strings would be refused field by field. The kind is what lets a value come back
/// as the value it was.
/// </summary>
public enum JournalKind
{
    /// <summary>There was no value. Absent and empty are the same thing to a journal.</summary>
    Nothing = 1,

    Text,
    Integer,
    Decimal,
    Boolean,
    Date,
    Timestamp,

    /// <summary>A <see cref="Ulid"/>: an identity, which is what a reference to another record is.</summary>
    Identity,

    /// <summary>Something the journal has no word for. Kept as its text, and not restorable.</summary>
    Unknown
}

/// <summary>
/// One value, as the journal keeps it (TR-2b).
///
/// A value is kept <b>whole</b> or it is kept as a <b>description of itself</b> - its length, a
/// hash and a preview - and which one it was is not a detail. A whole value can be put back; a
/// described one cannot, which is what turns a delete from <see cref="Reversibility.ReversibleWithConditions"/>
/// into <see cref="Reversibility.NotReversible"/> before it is allowed to run. Half an inverse
/// would be worse than none, because an undo that half-restores looks like one that worked.
/// </summary>
public sealed record JournalValue(JournalKind Kind, object? Value, int Length, string? Hash = null, string? Preview = null)
{
    /// <summary>No value: a field that was absent, or the empty side of an insert or a delete.</summary>
    public static readonly JournalValue Nothing = new(JournalKind.Nothing, null, 0);

    /// <summary>Whether the value itself is here, rather than a description of it.</summary>
    public bool IsWhole => Hash is null;

    public bool IsEmpty => Kind is JournalKind.Nothing;

    /// <summary>
    /// The value as the journal will keep it, given what it may spend on it.
    /// </summary>
    public static JournalValue Of(object? value, PayloadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (value is null) return Nothing;

        var kind = KindOf(value);
        var text = TextOf(value);

        if (text.Length <= limits.LongestValue) return new JournalValue(kind, value, text.Length);

        // Too large to keep. What is kept instead answers the three questions that are still
        // worth answering: how much there was, whether two of them are the same, and what it
        // looked like.
        return new JournalValue(
            kind,
            Value: null,
            text.Length,
            Hashes.Of(text),
            text[..Math.Min(limits.Preview, text.Length)]);
    }

    /// <summary>What this costs the change it is part of, for the cap in <see cref="PayloadLimits"/>.</summary>
    public int Weight => IsWhole ? Length : (Preview?.Length ?? 0) + (Hash?.Length ?? 0);

    /// <summary>
    /// The same value with the value itself given up: what is kept when the change as a whole is
    /// past its cap and the inverse is therefore not being kept at all (TR-2b).
    ///
    /// The hash stays because it is fixed width and still answers whether two of these were the
    /// same; the preview goes because a preview per field is what made the payload large. What
    /// this is not is a half-kept inverse - a change made of these is classified
    /// <see cref="Reversibility.NotReversible"/> before it runs, which is the point.
    /// </summary>
    public JournalValue WithoutValue() =>
        IsEmpty ? Nothing : new JournalValue(Kind, null, Length, Hash ?? Hashes.Of(TextOf(Value)));

    /// <summary>
    /// The value as text, for measuring, hashing and showing. Invariant throughout: a journal
    /// written on a machine that puts the fraction after a comma has to read the same as one
    /// written on a machine that does not, or two identical records hash differently.
    /// </summary>
    public static string TextOf(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    public static JournalKind KindOf(object? value) => value switch
    {
        null => JournalKind.Nothing,
        string => JournalKind.Text,
        long or int or short or sbyte or byte or ushort or uint or ulong => JournalKind.Integer,
        decimal or double or float => JournalKind.Decimal,
        bool => JournalKind.Boolean,
        DateOnly => JournalKind.Date,
        DateTime or DateTimeOffset => JournalKind.Timestamp,
        Ulid => JournalKind.Identity,
        _ => JournalKind.Unknown
    };
}

/// <summary>
/// One field of a change, old beside new (TR-2a).
///
/// Both sides are here even where one of them is <see cref="JournalValue.Nothing"/>, because the
/// detail panel shows them side by side and "this field did not exist before" and "this field was
/// empty before" are different things to a person looking at a record they did not expect.
/// </summary>
public sealed record FieldChange(string Name, JournalValue Before, JournalValue After)
{
    /// <summary>A field as a delete leaves it: it had a value, and now there is no record.</summary>
    public static FieldChange Removed(string name, JournalValue before) => new(name, before, JournalValue.Nothing);

    /// <summary>A field as an insert makes it.</summary>
    public static FieldChange Added(string name, JournalValue after) => new(name, JournalValue.Nothing, after);

    public int Weight => Name.Length + Before.Weight + After.Weight;
}
