using System.Globalization;

namespace TokkDb.LLM.Storage;

/// <summary>
/// Single place where a stored value becomes display text.
///
/// Both DisplayRule evaluation and the additional fields shown next to a display
/// value go through here, so the application has one formatting system rather
/// than two that can drift apart. Formatting is deterministic and culture
/// invariant; no LLM is involved.
/// </summary>
public static class RecordValueFormatter
{
    /// <summary>
    /// Converts a stored value to display text. Null renders as an empty string,
    /// never as the literal "null"; callers decide whether to omit the field.
    /// </summary>
    public static string Format(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text.Trim(),
            bool flag => flag ? "Yes" : "No",
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset =>
                dateTimeOffset.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()?.Trim() ?? string.Empty
        };
    }
}
