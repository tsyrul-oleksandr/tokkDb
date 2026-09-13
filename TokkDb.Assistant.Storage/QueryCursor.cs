using System.Globalization;
using System.Text;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// Where a page of results ended, so that the next one begins after it.
///
/// BR-3: paging is by cursor and never by offset. An offset re-counts from the start on every
/// page, so it costs more the further you go and it is wrong the moment anything is inserted
/// before the page you are on. A cursor says "after this record, in this order" and costs the
/// same on page one and page five hundred.
///
/// <b>It is the ordered index key, which is what makes ties safe.</b> The sort values alone are
/// not a position: ten thousand expenses with the same date are ten thousand records at the same
/// place, and a boundary among them would skip some and repeat others. The record's identity is
/// carried with them and the comparison is on the pair - exactly the composite key of value and
/// identity that the engine's own secondary indexes are built on (D-3 of the engine plan), which
/// is why this is cheap here and would not be somewhere else.
///
/// <b>What a cursor does not promise, stated rather than discovered.</b> A record whose sort
/// value changes between two pages moves across the cursor, and can then be seen twice or not at
/// all. Nothing short of multi-version reads fixes that, and the engine does not have them while
/// versioning is deferred, so BR-3b says it out loud and the table offers to start again.
/// </summary>
public sealed record QueryCursor
{
    // The unit separator: a character that is in no name, no number and no date, which is what
    // it was put in the character set for.
    private const char Separator = '\u001F';

    public QueryCursor(IReadOnlyList<object?> sortValues, Ulid recordId)
    {
        ArgumentNullException.ThrowIfNull(sortValues);

        SortValues = [.. sortValues];
        RecordId = recordId;
    }

    /// <summary>
    /// The values of the sort columns for the last record of the page, in the order the query
    /// asked for them. Empty when the query asked for no order, in which case identity is the
    /// order - which it always is underneath, since the identity is the last part of the key.
    /// </summary>
    public IReadOnlyList<object?> SortValues { get; }

    /// <summary>The identity of the last record of the page.</summary>
    public Ulid RecordId { get; }

    /// <summary>
    /// The cursor as text, so that it can travel: into a query result handle that survives a
    /// restart (QR-3a), into a trace step, or into a browser page that was closed and reopened.
    /// Round trips through <see cref="Parse"/>.
    ///
    /// The values carry their type, because "2024" read back as text would sort among the text
    /// and not among the years. That is the same reason the engine's key encoder puts a type tag
    /// in the first byte of every key it writes.
    /// </summary>
    public string Token
    {
        get
        {
            var parts = new List<string>(SortValues.Count + 1) { RecordId.ToString() };
            parts.AddRange(SortValues.Select(Encode));

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Separator, parts)));
        }
    }

    /// <summary>
    /// Reads a token back, or throws <see cref="InvalidDefinitionException"/> if it is not one.
    /// A token that does not parse is a refusal rather than an empty result, because silently
    /// starting from the beginning again is how a page sequence turns into a loop.
    /// </summary>
    public static QueryCursor Parse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            throw new InvalidDefinitionException("cursor", "That is not a page marker this storage issued.");
        }

        var parts = text.Split(Separator);

        if (parts.Length == 0 || !Ulid.TryParse(parts[0], out var id))
        {
            throw new InvalidDefinitionException("cursor", "That page marker does not name a record.");
        }

        return new QueryCursor([.. parts.Skip(1).Select(Decode)], id);
    }

    public override string ToString() => $"after {RecordId}";

    private static string Encode(object? value) => value switch
    {
        null => "0",
        string text => "s" + text,
        long whole => "i" + whole.ToString(CultureInfo.InvariantCulture),
        decimal exact => "d" + exact.ToString(CultureInfo.InvariantCulture),
        bool flag => "b" + (flag ? "1" : "0"),
        DateOnly day => "y" + day.ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => "t" + moment.ToString("O", CultureInfo.InvariantCulture),
        _ => "s" + value
    };

    private static object? Decode(string part)
    {
        if (part.Length == 0) return null;

        var rest = part[1..];

        return part[0] switch
        {
            's' => rest,
            'i' => long.Parse(rest, CultureInfo.InvariantCulture),
            'd' => decimal.Parse(rest, NumberStyles.Number, CultureInfo.InvariantCulture),
            'b' => rest == "1",
            'y' => DateOnly.Parse(rest, CultureInfo.InvariantCulture),
            't' => DateTime.Parse(rest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => null
        };
    }
}
