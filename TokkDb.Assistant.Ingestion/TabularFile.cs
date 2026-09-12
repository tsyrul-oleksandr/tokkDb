using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// A spreadsheet or a CSV file, read.
///
/// IN-1's whole surface: give it a file, get back what is in it, one <see cref="Table"/> per
/// sheet. Nothing here is asked of a model and nothing here is written anywhere. The same file
/// gives the same answer every time, which is what makes the awkward cases testable and what
/// makes the diagram able to say what the parse decided.
/// </summary>
public static class TabularFile
{
    /// <summary>Extensions this understands, for a caller deciding what to do with a file.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".csv", ".tsv", ".txt", ".xlsx", ".xlsm"];

    public static bool CanRead(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a file from disk.</summary>
    public static IReadOnlyList<Table> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);

        using var stream = File.OpenRead(path);

        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
            ? ReadWorkbook(stream)
            : ReadSeparatedValues(ReadAllBytes(stream), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Reads a workbook, one table per sheet that has anything in it.</summary>
    public static IReadOnlyList<Table> ReadWorkbook(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return
        [
            .. Xlsx.Read(stream)
                .Select(sheet => Assemble(
                    sheet.Name,
                    sheet.Rows,
                    new TableReading("xlsx", EncodingWasDeclared: true, Delimiter: null, 0, [], false)))
        ];
    }

    /// <summary>Reads a CSV or tab-separated file, working out its encoding and its delimiter.</summary>
    public static IReadOnlyList<Table> ReadSeparatedValues(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var (text, encoding, declared) = Csv.Decode(bytes);
        var delimiter = Csv.DetectDelimiter(text);
        var rows = Csv.Rows(text, delimiter).ToArray();

        return [Assemble(name, rows, new TableReading(encoding, declared, delimiter, 0, [], false))];
    }

    /// <summary>
    /// Rows into a table: find the header, keep what came before it, and work out what each
    /// column holds.
    /// </summary>
    private static Table Assemble(string name, IReadOnlyList<TableRow> rows, TableReading reading)
    {
        var header = Headers.Find(rows);

        var preamble = header.Yes
            ? rows.Take(header.Index)
                .Where(static row => !row.IsBlank)
                .Select(static row => string.Join(" ", row.Cells.Where(static cell => !string.IsNullOrWhiteSpace(cell))))
                .ToArray()
            : [];

        var body = rows
            .Skip(header.Yes ? header.Index + 1 : 0)
            .Where(static row => !row.IsBlank)
            .ToArray();

        var profiles = new List<ColumnProfile>(header.Columns.Count);

        for (var position = 0; position < header.Columns.Count; position++)
        {
            var column = position;

            profiles.Add(ColumnInference.Infer(
                header.Columns[position],
                position,
                [.. body.Select(row => (row.LineNumber, row[column]))]));
        }

        return new Table(
            name,
            header.Columns,
            body,
            profiles,
            reading with
            {
                HeaderLineNumber = header.LineNumber,
                Preamble = preamble,
                HeaderWasFound = header.Yes
            });
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream memory) return memory.ToArray();

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
