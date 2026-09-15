namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// A table pasted into what the person typed (UI-3): the lines that read as rows of one delimited
/// table are taken out and read as a file would be, and the lines before them stay as what was
/// said. Nobody should have to know that a table goes through the file path and a sentence
/// through the text path; the composer tells them apart here.
/// </summary>
public static class PastedTables
{
    private static readonly char[] Delimiters = ['\t', ';', ',', '|'];

    /// <summary>
    /// Splits a message into what was said and the table it carries, or returns false when there
    /// is no table: fewer than two lines share a delimiter, or the delimited lines do not agree on
    /// a width of at least two.
    /// </summary>
    public static bool TrySplit(string? message, out string said, out string table)
    {
        said = message?.Trim() ?? "";
        table = "";
        if (string.IsNullOrWhiteSpace(message)) return false;

        var lines = message.Replace("\r\n", "\n").Split('\n');
        foreach (var delimiter in Delimiters)
        {
            // The table starts at the first line holding the delimiter and runs to the end; a
            // wrapped row has fewer delimiters than the header, which the file reader rejoins.
            var start = Array.FindIndex(lines, line => line.Contains(delimiter));
            if (start < 0) continue;

            var rows = lines.Skip(start).Where(static line => line.Trim().Length > 0).ToList();
            var width = rows[0].Count(character => character == delimiter) + 1;
            if (width < 2 || rows.Count < 2) continue;

            var delimited = rows.Count(line => line.Contains(delimiter));
            if (delimited * 2 < rows.Count) continue;

            said = string.Join("\n", lines.Take(start)).Trim();
            table = string.Join("\n", rows);
            return true;
        }

        return false;
    }
}
