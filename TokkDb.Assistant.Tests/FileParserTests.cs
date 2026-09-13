using System.Text;
using TokkDb.Assistant.Ingestion;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// EX-3: adding a document format is implementing one interface and registering it in one place.
///
/// The acceptance condition is about what does <b>not</b> change, so that is what is asserted: a
/// parser for an extension nothing here has ever heard of is read by the same call the built-in
/// ones are read by, with nothing else touched.
/// </summary>
public sealed class FileParserTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A test that could not tidy up is not a test that failed.
            }
        }
    }

    private string AFile(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokkdb-parser-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content, Encoding.UTF8);
        _files.Add(path);
        return path;
    }

    [Fact]
    public void A_csv_file_is_read_as_tables()
    {
        var parsed = FileParsers.Read(AFile(".csv", "city,nights\nPrague,4\nLviv,2\n"));

        Assert.True(parsed.IsTabular);
        Assert.False(parsed.IsProse);

        var table = Assert.Single(parsed.Tables);
        Assert.Equal(["city", "nights"], table.Columns);
        Assert.Equal(2, table.RowCount);
    }

    [Fact]
    public void A_text_file_is_read_as_prose()
    {
        var parsed = FileParsers.Read(AFile(".txt", "# What happened\n\nWe went to Prague.\n"));

        Assert.True(parsed.IsProse);
        Assert.False(parsed.IsTabular);
        Assert.Equal(1, parsed.Prose!.HeadingCount);
    }

    [Fact]
    public void Every_format_the_plan_names_has_something_that_reads_it()
    {
        Assert.All(
            new[] { ".csv", ".xlsx", ".docx", ".txt" },
            extension => Assert.True(FileParsers.CanRead($"whatever{extension}"), extension));

        Assert.False(FileParsers.CanRead("whatever.pdf"));
    }

    [Fact]
    public void A_file_nothing_here_reads_is_refused_by_name()
    {
        var thrown = Assert.Throws<NotSupportedException>(() => FileParsers.Read(AFile(".pdf", "not really a pdf")));

        Assert.Contains(".pdf", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// EX-3's acceptance condition. The parser below is the whole of what a new format costs:
    /// one class, one registration, and the pipeline reads it without knowing it exists.
    /// </summary>
    [Fact]
    public void A_parser_for_a_new_extension_is_registered_in_one_place_and_used()
    {
        Assert.False(FileParsers.CanRead("notes.jsonl"));

        FileParsers.Register(new LineParser());

        try
        {
            Assert.True(FileParsers.CanRead("notes.jsonl"));

            var parsed = FileParsers.Read(AFile(".jsonl", "one\ntwo\nthree\n"));

            Assert.True(parsed.IsProse);
            Assert.Equal(3, parsed.Prose!.Blocks.Count);
        }
        finally
        {
            Reset();
        }
    }

    /// <summary>A format nothing else here has heard of, read as one block per line.</summary>
    private sealed class LineParser : IFileParser
    {
        public IReadOnlyList<string> Extensions => [".jsonl"];

        public ParsedFile Read(Stream content, string name)
        {
            using var reader = new StreamReader(content, Encoding.UTF8);

            var blocks = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static line => new ProseBlock(ProseBlockKind.Paragraph, line))
                .ToArray();

            return ParsedFile.OfProse(name, ".jsonl", new ProseDocument(name, blocks, "utf-8"));
        }
    }

    /// <summary>
    /// The registry is static, which is what "one place" means and what makes a test that adds to
    /// it have to take it back out again.
    /// </summary>
    private static void Reset()
    {
        var registered = FileParsers.All.OfType<LineParser>().ToList();

        foreach (var parser in registered) FileParsers.Remove(parser);
    }
}
