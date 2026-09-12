using System.Text;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// Reading a CSV file: what it is encoded in, what separates its fields, and the fields.
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
    /// What the bytes say, or the best guess and a note that it was one.
    ///
    /// A byte order mark settles it. Failing that, valid UTF-8 is UTF-8 - the encoding is
    /// self-checking enough that a file which decodes cleanly almost certainly is one. Failing
    /// that the file is in some single-byte code page and nothing in it says which, so the
    /// candidates are scored on how much of what they produce looks like writing rather than
    /// like symbols and control characters.
    /// </summary>
    public static (string Text, string EncodingName, bool Declared) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8", true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16", true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16BE", true);
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "utf-8", false);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8, so it is one byte per character and the file does not say which set.
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var best = ("", "", double.MinValue);

        foreach (var codePage in new[] { 1252, 1251, 1250 })
        {
            string decoded;
            try
            {
                decoded = Encoding.GetEncoding(codePage).GetString(bytes);
            }
            catch (NotSupportedException)
            {
                continue;
            }

            var score = Readability(decoded);
            if (score > best.Item3) best = (decoded, $"windows-{codePage}", score);
        }

        return best.Item1.Length > 0
            ? (best.Item1, best.Item2, false)
            : (Encoding.Latin1.GetString(bytes), "iso-8859-1", false);
    }

    /// <summary>
    /// How much of a decoded string looks like something somebody wrote. Letters, digits,
    /// spaces and ordinary punctuation count for it; control characters and the symbol-and-
    /// dingbat range a wrong code page produces count against it.
    /// </summary>
    private static double Readability(string text)
    {
        if (text.Length == 0) return 0;

        var good = 0;
        var bad = 0;

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || char.IsPunctuation(character))
            {
                good++;
            }
            else if (char.IsControl(character) || char.IsSymbol(character) || character == '�')
            {
                bad++;
            }
        }

        return (good - 2.0 * bad) / text.Length;
    }

    /// <summary>
    /// Which character separates the fields. The one that gives the most fields per line while
    /// giving the same number on every line: a delimiter that is really a delimiter divides
    /// every row the same way, and one that happens to appear in the text does not.
    /// </summary>
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
