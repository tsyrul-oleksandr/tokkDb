namespace TokkDb.Assistant.Ingestion;

/// <summary>Where the table starts, and what the columns are called.</summary>
internal readonly record struct HeaderFound(
    bool Yes,
    int Index,
    int LineNumber,
    IReadOnlyList<string> Columns,
    int Width);

/// <summary>
/// Finding the row the table actually starts at.
///
/// A file exported by a person rather than by a machine begins with a title, a date it was run,
/// a blank line, and then the table. Nothing marks where the preamble ends, so it has to be
/// recognised: the header is the first row that is as wide as the table, is mostly not blank,
/// has at least one word in it, holds nothing that is a number or a date or an answer, and is
/// followed by a row of the same width.
///
/// Every one of those is doing work, and the last two do most of it. Nothing-that-is-a-value is
/// what separates a header from the first row of a table that has no header at all: a header of
/// <c>Date, Amount, Notes</c> is three pieces of text, and a first data row is not.
/// At-least-one-word is what stops a row of dates and amounts being taken for names when it is
/// read as a single column.
///
/// Two conditions are deliberately <b>not</b> here, having been tried and removed. A name in
/// every cell: a spreadsheet with an unlabelled column is ordinary, and the blank gets a name of
/// its own below rather than disqualifying the row. No two cells the same: a spreadsheet with two
/// columns called Amount is also ordinary, and it gets the same treatment. Both rules rejected
/// real headers, and a rejected header does not degrade gracefully - the first row of data
/// becomes the names, or the file is read as one column.
///
/// A file with no header gets columns called after their positions and keeps every row, which is
/// better than taking its first row of data and calling it the names.
/// </summary>
internal static class Headers
{
    public static HeaderFound Find(IReadOnlyList<TableRow> rows)
    {
        var populated = rows.Where(static row => !row.IsBlank).ToArray();
        if (populated.Length == 0) return new HeaderFound(false, -1, 0, [], 0);

        // How wide the table is: the width most of its rows share.
        var width = populated
            .GroupBy(static row => row.Cells.Count)
            .OrderByDescending(static group => group.Count())
            .ThenByDescending(static group => group.Key)
            .First()
            .Key;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.IsBlank || row.Cells.Count != width) continue;
            if (!LooksLikeNames(row)) continue;

            // A header with nothing under it is a row of text, not a header.
            var hasBody = rows.Skip(i + 1).Any(candidate => !candidate.IsBlank && candidate.Cells.Count == width);
            if (!hasBody) continue;

            return new HeaderFound(
                true,
                i,
                row.LineNumber,
                [.. Named(row)],
                width);
        }

        return new HeaderFound(
            false,
            -1,
            populated[0].LineNumber,
            [.. Enumerable.Range(1, width).Select(static position => $"column {position}")],
            width);
    }

    private static bool LooksLikeNames(TableRow row)
    {
        var cells = row.Cells;

        var named = cells.Count(static cell => !string.IsNullOrWhiteSpace(cell));

        // Mostly named. A row with one heading and four empty cells is a title, not a header.
        if (named * 2 < cells.Count || named == 0) return false;

        var names = cells
            .Where(static cell => !string.IsNullOrWhiteSpace(cell))
            .Select(static cell => cell!.Trim())
            .ToArray();

        // A name is a word, not a value. Anything a column could hold as data is not a name for
        // that column - and a heading that is only digits is a year or an amount somebody put
        // above the table.
        foreach (var name in names)
        {
            if (Numbers.Read(name).IsNumber) return false;
            if (Moments.Read(name).IsMoment) return false;
            if (Booleans.Read(name) is not null) return false;
        }

        // At least one of them is a word. Without this, a row of dates and amounts read as a
        // single cell looks like a name, and every file would be one column with a header.
        return names.Any(static name => name.Any(char.IsLetter));
    }

    /// <summary>
    /// The names, made usable: trimmed, and anything blank or repeated given a name of its own so
    /// that two columns never end up called the same thing.
    /// </summary>
    private static IEnumerable<string> Named(TableRow row)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < row.Cells.Count; i++)
        {
            var name = row.Cells[i]?.Trim();
            if (string.IsNullOrEmpty(name)) name = $"column {i + 1}";

            if (seen.TryGetValue(name, out var times))
            {
                seen[name] = times + 1;
                yield return $"{name} {times + 1}";
                continue;
            }

            seen[name] = 1;
            yield return name;
        }
    }
}
