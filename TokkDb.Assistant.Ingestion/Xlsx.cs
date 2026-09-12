using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// Reading a workbook, one sheet at a time and one row at a time.
///
/// This is the whole cost R-5 named, in one file. <c>DocumentFormat.OpenXml</c> hands back the
/// format as it is, so three things a friendlier library would have hidden have to be done here:
///
/// <list type="number">
/// <item><b>Shared strings.</b> Text is not in the sheet. The sheet holds an index into a table
/// of every distinct string in the workbook, which is read once and kept.</item>
/// <item><b>Dates are numbers.</b> There is no date in a worksheet - there is a number of days
/// since the epoch and a formatting rule saying to show it as a date. Whether a cell is a date
/// is a property of its style, not of its value, so the styles have to be read and the built-in
/// and custom number formats told apart.</item>
/// <item><b>Rows are sparse.</b> A row with nothing in column B does not hold an empty cell for
/// it; it holds A and then C. The cell references have to be decoded back into positions or
/// every value after a gap lands in the wrong column.</item>
/// </list>
///
/// Reading is through <c>OpenXmlReader</c>, which walks the part forward without building it, so
/// the fifty-thousand-row file of IN-3 costs one row at a time rather than a document.
/// </summary>
internal static class Xlsx
{
    /// <summary>Every sheet that has anything in it, with its cells as text.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<TableRow> Rows)> Read(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);

        var workbook = document.WorkbookPart
            ?? throw new InvalidDataException("The file is not a workbook.");

        var strings = SharedStrings(workbook);
        var dateStyles = DateStyles(workbook);
        var sheets = new List<(string, IReadOnlyList<TableRow>)>();

        // A package that opened but has no workbook part contents is a file that says it is a
        // spreadsheet and is not one.
        var listed = workbook.Workbook?.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>();

        foreach (var sheet in listed)
        {
            if (sheet.Id?.Value is not { } relationship) continue;
            if (workbook.GetPartById(relationship) is not WorksheetPart part) continue;

            var rows = ReadSheet(part, strings, dateStyles);
            if (rows.Count == 0) continue;

            sheets.Add((sheet.Name?.Value ?? $"sheet {sheets.Count + 1}", rows));
        }

        return sheets;
    }

    private static List<TableRow> ReadSheet(
        WorksheetPart part,
        IReadOnlyList<string> strings,
        IReadOnlySet<uint> dateStyles)
    {
        var rows = new List<TableRow>();
        using var reader = OpenXmlReader.Create(part);

        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;

            var row = (Row)reader.LoadCurrentElement()!;

            // The sheet's own row number, so that "row 47" means the row a person would find if
            // they opened the file - the same promise the CSV reader's line numbers make.
            var lineNumber = row.RowIndex?.Value is { } index ? (int)index : rows.Count + 1;

            var cells = new List<string?>();

            foreach (var cell in row.Elements<Cell>())
            {
                var position = ColumnOf(cell.CellReference?.Value);

                // The gaps a sparse row leaves behind.
                while (position >= 0 && cells.Count < position) cells.Add(null);

                cells.Add(Text(cell, strings, dateStyles));
            }

            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace))
            {
                // Kept, because a blank line is part of where the preamble ends.
                rows.Add(new TableRow(lineNumber, []));
                continue;
            }

            rows.Add(new TableRow(lineNumber, cells));
        }

        // Trailing blank rows are the sheet's padding rather than the table's.
        while (rows.Count > 0 && rows[^1].IsBlank) rows.RemoveAt(rows.Count - 1);

        return rows;
    }

    private static string? Text(Cell cell, IReadOnlyList<string> strings, IReadOnlySet<uint> dateStyles)
    {
        var raw = cell.CellValue?.InnerText;

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                   && index >= 0 && index < strings.Count
                ? strings[index]
                : null;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.Text?.Text ?? cell.InnerText;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return raw == "1" ? "true" : "false";
        }

        if (cell.DataType?.Value == CellValues.Error) return null;
        if (string.IsNullOrEmpty(raw)) return null;

        if (cell.DataType?.Value == CellValues.String) return raw;

        // A number, and its style says whether it is meant to be a date.
        if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            return FromSerial(serial);
        }

        return raw;
    }

    /// <summary>
    /// A worksheet date is a count of days from the last day of 1899, with a deliberate mistake
    /// in it: 1900 is treated as a leap year for compatibility with a spreadsheet from 1983, so
    /// serial 60 is a day that never happened and everything after it is shifted by one.
    /// <see cref="DateTime.FromOADate"/> knows this and is used rather than re-deriving it.
    /// </summary>
    private static string FromSerial(double serial)
    {
        if (serial is < 1 or > 2_958_465) return serial.ToString(CultureInfo.InvariantCulture);

        var moment = DateTime.FromOADate(serial);

        return moment.TimeOfDay == TimeSpan.Zero
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static List<string> SharedStrings(WorkbookPart workbook)
    {
        var table = workbook.SharedStringTablePart?.SharedStringTable;
        return table is null ? [] : [.. table.Elements<SharedStringItem>().Select(static item => item.InnerText)];
    }

    /// <summary>
    /// Which cell styles mean "this number is a date".
    ///
    /// Two kinds. The built-in format identifiers 14 to 22 and 45 to 47 are dates and times by
    /// definition and carry no format string. Everything else is a format somebody wrote, and it
    /// is a date if it has date or time tokens in it - looked for outside the quoted literals a
    /// format may also contain, so a currency format of <c>"dd" #,##0</c> is not mistaken for one.
    /// </summary>
    private static HashSet<uint> DateStyles(WorkbookPart workbook)
    {
        var dateStyles = new HashSet<uint>();
        var styles = workbook.WorkbookStylesPart?.Stylesheet;
        if (styles?.CellFormats is null) return dateStyles;

        var custom = new Dictionary<uint, string>();
        foreach (var format in styles.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id && format.FormatCode?.Value is { } code)
            {
                custom[id] = code;
            }
        }

        uint index = 0;
        foreach (var format in styles.CellFormats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;

            if (id is (>= 14 and <= 22) or (>= 45 and <= 47))
            {
                dateStyles.Add(index);
            }
            else if (custom.TryGetValue(id, out var code) && LooksLikeADate(code))
            {
                dateStyles.Add(index);
            }

            index++;
        }

        return dateStyles;
    }

    private static bool LooksLikeADate(string formatCode)
    {
        var inQuotes = false;

        for (var i = 0; i < formatCode.Length; i++)
        {
            var character = formatCode[i];

            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            // A backslash escapes the next character, which is then a literal and not a token.
            if (character == '\\')
            {
                i++;
                continue;
            }

            if (inQuotes) continue;

            if (character is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S') return true;

            // 'm' is minutes as well as months, and both are dates.
            if (character is 'm' or 'M') return true;
        }

        return false;
    }

    /// <summary>The zero-based column a cell reference names: A is 0, Z is 25, AA is 26.</summary>
    private static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;

        var column = 0;

        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character)) break;
            column = column * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return column - 1;
    }
}
