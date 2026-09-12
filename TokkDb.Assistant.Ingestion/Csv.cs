using System.Text;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// Reading a CSV file: what separates its fields, and the fields.
///
/// <b>Written here rather than taken from a library.</b> Three things are wanted that a general
/// CSV library does not give together: a delimiter worked out from the file rather than
/// configured, a line number on every row so that IN-4 can say which row it could not store, and
/// a parser that never throws on a malformed row but hands it back as it found it. The format
/// itself is a page of rules. A dependency that had to be fought on all three would cost more
/// than it saved.
/// </summary>
internal static class Csv
{
    /// <summary>The characters worth considering, in the order they are worth considering.</summary>
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>How many lines are enough to tell what the delimiter is.</summary>
    private const int LinesSampledForDelimiter = 20;

    /// <summary>
    /// Which character separates the fields, or null when the file has one column.
    ///
    /// The test is not how consistent the row widths look. A file of one column holding
    /// <c>0,75</c> and <c>1,5</c> splits into two columns as consistently as a real
    /// comma-separated file does, and no amount of counting will tell them apart.
    ///
    /// What tells them apart is whether the result has a header. A delimiter that is really the
    /// delimiter produces rows whose names are at the top; one that is really part of the values
    /// produces columns of fragments with no row that looks like names above them. So each
    /// candidate is tried, the header is looked for, and the <b>widest</b> candidate that finds
    /// one wins.
    ///
    /// Width is the tie-break rather than an afterthought, because a candidate that does not
    /// appear in the file at all leaves every line whole - which looks exactly like a file of one
    /// column, and a line of prose looks exactly like a name. Such a candidate therefore always
    /// "finds a header", and it has to lose to any real delimiter that also finds one.
    ///
    /// Failing all of that, the most consistent candidate, because a file with no header still
    /// has to be split somehow. And if the winner leaves one field per line, the file has one
    /// column and no delimiter to name, whichever candidate happened to win.
    /// </summary>
    public static char? DetectDelimiter(string text)
    {
        var withHeader = (Delimiter: (char?)null, Width: 0, Found: false);
        var withoutHeader = (Delimiter: (char?)null, Width: 0, Agreeing: 0);

        foreach (var candidate in Candidates.Select(static character => (char?)character))
        {
            var rows = Rows(text, candidate).Take(LinesSampledForDelimiter).ToArray();
            var populated = rows.Where(static row => !row.IsBlank).ToArray();
            if (populated.Length == 0) continue;

            var modal = populated
                .GroupBy(static row => row.Cells.Count)
                .OrderByDescending(static group => group.Count())
                .ThenByDescending(static group => group.Key)
                .First();

            var header = Headers.Find(rows);

            if (header.Yes && header.Width > withHeader.Width)
            {
                withHeader = (candidate, header.Width, true);
            }

            if (!withHeader.Found && modal.Key > withoutHeader.Width && modal.Count() * 2 >= populated.Length)
            {
                withoutHeader = (candidate, modal.Key, modal.Count());
            }
        }

        var (delimiter, width) = withHeader.Found
            ? (withHeader.Delimiter, withHeader.Width)
            : (withoutHeader.Delimiter, withoutHeader.Width);

        // One field per line is one column, and nothing divided it.
        return width > 1 ? delimiter : null;
    }

    /// <summary>
    /// The rows, as RFC 4180 describes them: fields separated by the delimiter, a field may be
    /// quoted, a quoted field may contain the delimiter, a newline or a doubled quote.
    ///
    /// It never throws. A quote that is never closed ends at the end of the file and the row is
    /// handed back as it stands, because IN-4 wants a bad row reported rather than an import
    /// abandoned.
    /// </summary>
    public static IEnumerable<TableRow> Rows(string text, char? delimiter)
    {
        var cells = new List<string?>();
        var cell = new StringBuilder();
        var line = 1;
        var rowStartedAt = 1;
        var quoted = false;
        var any = false;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];

            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                        continue;
                    }

                    quoted = false;
                    continue;
                }

                if (character == '\n') line++;
                cell.Append(character);
                any = true;
                continue;
            }

            if (character == '"' && cell.Length == 0)
            {
                quoted = true;
                any = true;
                continue;
            }

            if (character == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                any = true;
                continue;
            }

            if (character is '\r' or '\n')
            {
                // \r\n is one ending, not two.
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;

                cells.Add(cell.ToString());
                cell.Clear();

                yield return new TableRow(rowStartedAt, cells);

                cells = [];
                line++;
                rowStartedAt = line;
                any = false;
                continue;
            }

            cell.Append(character);
            any = true;
        }

        if (any || cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            yield return new TableRow(rowStartedAt, cells);
        }
    }
}
