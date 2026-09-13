using System.Globalization;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// The one field by which a person recognises a record (SC-11).
///
/// BR-2 leads its table with it and BR-5 titles the record with it, so it has to exist for every
/// record of every collection, including one created before anybody thought about how its records
/// should read. Hence a rule with fallbacks rather than a property that can be null:
///
/// <list type="number">
/// <item>the collection's <see cref="DisplayRule"/>, where one is set;</item>
/// <item>otherwise the first <b>required</b> text column, because a column that must have a value
/// is the one the collection's author thought a record could not do without;</item>
/// <item>otherwise the first text column;</item>
/// <item>otherwise the identity, shortened - which names the record without pretending to
/// describe it.</item>
/// </list>
///
/// <b>Why it is derived and not stored.</b> A stored display value is a copy that goes stale the
/// moment the field it came from is edited, and there would then be two answers to what a record
/// is called. Deriving it costs a dictionary lookup, and changing the rule rewrites no records -
/// which is why SC-11 can call that change <c>Safe</c> under D-14.
/// </summary>
public static class DisplayValue
{
    /// <summary>How many characters of the identity a fallback title shows.</summary>
    public const int ShortenedIdentityLength = 8;

    /// <summary>What this record is called.</summary>
    public static string For(CollectionDefinition definition, StorageRecord record)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(record);

        if (definition.DisplayRule is { } rule)
        {
            var rendered = Render(rule, record);
            if (!string.IsNullOrWhiteSpace(rendered)) return rendered;
        }

        if (Column(definition) is { } column && record[column.Name] is { } value)
        {
            var text = Text(value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return Shortened(record.Id);
    }

    /// <summary>
    /// The column a record is recognised by when there is no display rule, or null when the
    /// collection has no text in it at all. Used by the browser to decide which column leads the
    /// table, which is the same question asked about a collection rather than about a record.
    /// </summary>
    public static ColumnDefinition? Column(CollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var column in definition.Columns)
        {
            if (column is { Type: ColumnType.Text, Required: true }) return column;
        }

        foreach (var column in definition.Columns)
        {
            if (column.Type is ColumnType.Text) return column;
        }

        return null;
    }

    /// <summary>The identity, short enough to sit in a table cell and long enough to tell two apart.</summary>
    public static string Shortened(Ulid id)
    {
        var text = id.ToString();
        return text.Length <= ShortenedIdentityLength ? text : text[^ShortenedIdentityLength..];
    }

    /// <summary>
    /// A display rule filled in from a record: the literal parts as they are, the column
    /// references replaced by what the record holds. A column the record has no value for
    /// contributes nothing rather than the word "null", because the line is read by a person.
    /// </summary>
    private static string Render(DisplayRule rule, StorageRecord record) =>
        string.Concat(rule.Segments.Select(segment =>
            segment.IsColumnReference ? Text(record[segment.Text]) : segment.Text)).Trim();

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "yes" : "no",
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
