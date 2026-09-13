namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// What came out of one file: tables, or prose, or in principle both.
///
/// One type for both because EX-3 asks for one parser interface, and a parser interface that
/// returned two different things would be two interfaces with one name. A spreadsheet produces
/// tables and no prose; a document produces prose and no tables; a format that someday produces
/// both - a report with a table in it - has somewhere to put them without the interface changing.
/// </summary>
public sealed record ParsedFile(
    string Name,
    string Extension,
    IReadOnlyList<Table> Tables,
    ProseDocument? Prose)
{
    public static ParsedFile Tabular(string name, string extension, IReadOnlyList<Table> tables) =>
        new(name, extension, tables, null);

    public static ParsedFile OfProse(string name, string extension, ProseDocument prose) =>
        new(name, extension, [], prose);

    public bool IsTabular => Tables.Count > 0;

    public bool IsProse => Prose is not null;
}

/// <summary>
/// One document format, read (EX-3).
///
/// The whole of what a new format has to implement: which extensions it answers to, and how to
/// turn a stream into a <see cref="ParsedFile"/>. Nothing about models, nothing about storage,
/// nothing about where the file came from - a parser is handed bytes and a name, which is what
/// makes it testable from a byte array and what keeps the pipeline from having to know which
/// kind of file it is holding.
/// </summary>
public interface IFileParser
{
    /// <summary>The extensions this reads, with their dots: <c>.csv</c>.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Reads the stream. The name is the file's, without its extension, for the table or document.</summary>
    ParsedFile Read(Stream content, string name);
}

/// <summary>
/// The parsers, in one place (EX-3).
///
/// <b>The one place.</b> Adding a format is implementing <see cref="IFileParser"/> and adding it
/// to the list below - or calling <see cref="Register"/>, for a parser that arrives from
/// somewhere else. Nothing downstream changes: the pipeline asks this which parser reads a file
/// and gets a <see cref="ParsedFile"/> back, and it has never known what formats exist.
///
/// <b>Where <c>.txt</c> goes, since two parsers could claim it.</b> To prose. A text file is
/// nearly always something somebody wrote, and a tab-separated file that happens to be called
/// <c>.txt</c> is the rare case - which is served by handing the bytes to
/// <see cref="TabularFile.ReadSeparatedValues"/> directly, a caller that knows what it has
/// rather than a guess made here.
/// </summary>
public static class FileParsers
{
    private static readonly List<IFileParser> Parsers =
    [
        new SeparatedValuesParser(),
        new WorkbookParser(),
        new WordParser(),
        new PlainTextParser()
    ];

    /// <summary>Every parser, in the order they are asked.</summary>
    public static IReadOnlyList<IFileParser> All => Parsers;

    /// <summary>Every extension anything here reads.</summary>
    public static IReadOnlyList<string> Extensions =>
        [.. Parsers.SelectMany(static parser => parser.Extensions).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Adds a parser. It is asked before the built-in ones, so a caller can replace how a format
    /// is read without the format having to be removed from the list above.
    /// </summary>
    public static void Register(IFileParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Parsers.Insert(0, parser);
    }

    /// <summary>
    /// Takes a registered parser back out again, returning false if it was not there. For a
    /// caller that replaced a format for the length of something and wants the built-in one back.
    /// </summary>
    public static bool Remove(IFileParser parser) => Parsers.Remove(parser);

    /// <summary>The parser that reads this file, or null if nothing here does.</summary>
    public static IFileParser? For(string path)
    {
        var extension = Path.GetExtension(path);

        return Parsers.FirstOrDefault(parser =>
            parser.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    public static bool CanRead(string path) => For(path) is not null;

    /// <summary>Reads a file from disk with whichever parser answers to its extension.</summary>
    /// <exception cref="NotSupportedException">Nothing here reads that kind of file.</exception>
    public static ParsedFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var parser = For(path)
            ?? throw new NotSupportedException($"There is nothing here that reads a '{Path.GetExtension(path)}' file.");

        using var stream = File.OpenRead(path);

        return parser.Read(stream, Path.GetFileNameWithoutExtension(path));
    }
}

/// <summary>CSV and its tab-separated cousin. See <see cref="TabularFile"/>.</summary>
public sealed class SeparatedValuesParser : IFileParser
{
    public IReadOnlyList<string> Extensions => [".csv", ".tsv"];

    public ParsedFile Read(Stream content, string name)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        return ParsedFile.Tabular(name, ".csv", TabularFile.ReadSeparatedValues(buffer.ToArray(), name));
    }
}

/// <summary>A workbook, one table per sheet. See <see cref="TabularFile"/>.</summary>
public sealed class WorkbookParser : IFileParser
{
    public IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];

    public ParsedFile Read(Stream content, string name) =>
        ParsedFile.Tabular(name, ".xlsx", TabularFile.ReadWorkbook(content));
}

/// <summary>A Word document. See <see cref="ProseFile"/>.</summary>
public sealed class WordParser : IFileParser
{
    public IReadOnlyList<string> Extensions => [".docx", ".docm"];

    public ParsedFile Read(Stream content, string name) =>
        ParsedFile.OfProse(name, ".docx", ProseFile.ReadWord(content, name));
}

/// <summary>A text file, which may be Markdown and is treated the same way either way.</summary>
public sealed class PlainTextParser : IFileParser
{
    public IReadOnlyList<string> Extensions => [".txt", ".md"];

    public ParsedFile Read(Stream content, string name)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        return ParsedFile.OfProse(name, ".txt", ProseFile.ReadText(buffer.ToArray(), name));
    }
}
