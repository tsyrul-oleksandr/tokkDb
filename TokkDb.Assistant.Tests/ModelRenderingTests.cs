using System.Text;
using TokkDb.Assistant.Ingestion;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// IN-3: what reaches a model is a rendering chosen for the job, never the file.
///
/// The requirement's acceptance condition is a bound that does not move with the size of the
/// input, so that is what is asserted - not that the rendering is small, which any truncation
/// would satisfy, but that a file a hundred times larger produces the same thing.
/// </summary>
public sealed class ModelRenderingTests
{
    /// <summary>
    /// A spreadsheet of the given size, with the same shape either way: a name, a date, an
    /// amount and a category that repeats.
    /// </summary>
    private static Table Spreadsheet(int rows)
    {
        var text = new StringBuilder("Event,Paid on,Amount,Category\n");

        for (var i = 0; i < rows; i++)
        {
            text.Append("Event ").Append(i).Append(',')
                .Append("2026-07-").Append((i % 28 + 1).ToString("00")).Append(',')
                .Append(100 + (i % 900)).Append('.').Append((i % 100).ToString("00")).Append(',')
                .Append((i % 3) switch { 0 => "travel", 1 => "accommodation", _ => "tickets" })
                .Append('\n');
        }

        return Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(text.ToString()), "expenses"));
    }

    /// <summary>
    /// The requirement's own condition. Fifty thousand rows and five hundred rows produce the
    /// same rendering but for the number of rows written in it, which is the one thing that
    /// should differ.
    /// </summary>
    [Fact]
    public void A_file_of_fifty_thousand_rows_renders_the_same_as_one_of_five_hundred()
    {
        var small = TableRendering.Render(Spreadsheet(500));
        var large = TableRendering.Render(Spreadsheet(50_000));

        // Both are inside the budget, which is the bound that matters.
        Assert.True(
            Tokens.Estimate(large) <= RenderingBudget.Mapping.Tokens,
            $"the rendering was {Tokens.Estimate(large)} tokens against a budget of {RenderingBudget.Mapping.Tokens}");

        // And the only difference between them is the digits of the counts. Those are
        // information about the file rather than a share of it: "50000 rows, 3 distinct
        // categories" is what tells a model this is a category and not an identifier, and it
        // costs two characters more to say than "500 rows" does.
        Assert.Contains("rows: 500\n", small, StringComparison.Ordinal);
        Assert.Contains("rows: 50000\n", large, StringComparison.Ordinal);

        Assert.True(
            Math.Abs(large.Length - small.Length) <= 20,
            $"the rendering grew by {large.Length - small.Length} characters for a hundred times the rows");

        // Same shape, line for line, and the same number of sample rows.
        Assert.Equal(small.Split('\n').Length, large.Split('\n').Length);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    [InlineData(5_000)]
    [InlineData(50_000)]
    public void The_rendering_stays_inside_its_budget_whatever_the_file(int rows)
    {
        var rendering = TableRendering.Render(Spreadsheet(rows));

        Assert.True(
            Tokens.Estimate(rendering) <= RenderingBudget.Mapping.Tokens,
            $"{rows} rows rendered to {Tokens.Estimate(rendering)} tokens");
    }

    /// <summary>
    /// The rendering is a description with a sample attached, and the description is the half
    /// that earns its tokens: it says things about the fifty thousand rows that the five in the
    /// sample cannot.
    /// </summary>
    [Fact]
    public void The_rendering_says_what_the_columns_are_and_shows_a_few_rows()
    {
        var rendering = TableRendering.Render(Spreadsheet(50_000));

        Assert.Contains("table: expenses", rendering, StringComparison.Ordinal);
        Assert.Contains("Paid on: date", rendering, StringComparison.Ordinal);
        Assert.Contains("Amount: number", rendering, StringComparison.Ordinal);
        Assert.Contains("Category: text", rendering, StringComparison.Ordinal);

        // Three distinct categories in fifty thousand rows is the difference between a category
        // and an identifier, and no sample of five rows establishes it.
        Assert.Contains("Category: text, 3 distinct", rendering, StringComparison.Ordinal);

        Assert.Contains("sample of the rows:", rendering, StringComparison.Ordinal);
        // The sampled rows, not counting the line of column names above them.
        Assert.Equal(
            TableRendering.SampleRows,
            rendering.Split('\n').Count(static line =>
                line.StartsWith("  Event ", StringComparison.Ordinal) && char.IsAsciiDigit(line[8])));
    }

    /// <summary>
    /// The part a sample could not have given. Two bad values in a thousand rows are not in the
    /// first five, and they are the two the user has to be asked about.
    /// </summary>
    [Fact]
    public void The_rendering_names_the_values_that_did_not_fit_however_far_down_the_file_they_are()
    {
        var text = new StringBuilder("Event,Nights\n");

        for (var i = 0; i < 1_000; i++)
        {
            text.Append("Event ").Append(i).Append(',').Append(i == 640 ? "pending" : (i % 5).ToString()).Append('\n');
        }

        var table = Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(text.ToString()), "trips"));
        var rendering = TableRendering.Render(table);

        Assert.Contains("Nights: text", rendering, StringComparison.Ordinal);
        Assert.Contains("mostly whole number (over 99%), but 1 value is not:", rendering, StringComparison.Ordinal);
        Assert.Contains("row 642 \"pending\"", rendering, StringComparison.Ordinal);
    }

    /// <summary>
    /// A share is never rounded up to all of it. "100%" printed beside "but 1 value is not" tells
    /// the model two things that cannot both be true, and it has to pick one.
    /// </summary>
    [Fact]
    public void A_column_that_is_almost_entirely_one_thing_does_not_claim_to_be_entirely_it()
    {
        var text = new StringBuilder("Event,Nights\n");

        for (var i = 0; i < 5_000; i++)
        {
            text.Append("Event ").Append(i).Append(',').Append(i == 4_000 ? "pending" : "2").Append('\n');
        }

        var table = Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(text.ToString()), "trips"));
        var rendering = TableRendering.Render(table);

        Assert.Contains("over 99%", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("(100%)", rendering, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rendering_carries_what_was_written_above_the_table()
    {
        var table = Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(
            """
            Expense report - Q3

            Event,Amount
            EuroPython,840.50
            DevDays,600
            """), "file"));

        Assert.Contains("said above the table: Expense report - Q3", TableRendering.Render(table), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file wide enough that describing its columns spends the budget. The description wins and
    /// the sample is dropped, because knowing what two hundred columns are is worth more than
    /// seeing five rows of them - and the count of what was left out is said rather than implied.
    /// </summary>
    [Fact]
    public void A_very_wide_file_spends_its_budget_on_the_columns_rather_than_the_rows()
    {
        var names = Enumerable.Range(1, 200).Select(static i => $"measurement_number_{i}").ToArray();
        var values = Enumerable.Range(1, 200).Select(static i => (i * 7 % 100).ToString()).ToArray();

        var text = string.Join(',', names) + "\n" + string.Join(',', values) + "\n" + string.Join(',', values);

        var table = Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(text), "wide"));
        var rendering = TableRendering.Render(table);

        Assert.True(
            Tokens.Estimate(rendering) <= RenderingBudget.Mapping.Tokens,
            $"a 200-column file rendered to {Tokens.Estimate(rendering)} tokens");

        Assert.Contains("more columns", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("sample of the rows:", rendering, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_header_says_so_rather_than_offering_its_positions_as_names()
    {
        var table = Assert.Single(TabularFile.ReadSeparatedValues(
            Encoding.UTF8.GetBytes("2026-07-20,840.50\n2026-08-03,312.00"), "file"));

        Assert.Contains("the file has no header row", TableRendering.Render(table), StringComparison.Ordinal);
    }

    // ---- Prose ------------------------------------------------------------------------------

    private static ProseDocument Document(int paragraphs)
    {
        var text = new StringBuilder("# Expense policy\n\n");

        for (var i = 0; i < paragraphs; i++)
        {
            if (i % 10 == 0) text.Append("## Section ").Append(i / 10).Append("\n\n");

            text.Append("Paragraph ").Append(i)
                .Append(" says something about what may be claimed and under what conditions. ")
                .Append("It runs to two sentences so that it is long enough to be worth breaking between.\n\n");
        }

        return ProseFile.ReadText(Encoding.UTF8.GetBytes(text.ToString()));
    }

    [Fact]
    public void Every_chunk_of_a_long_document_is_inside_the_budget()
    {
        var chunks = ProseChunking.Chunk(Document(400));

        Assert.True(chunks.Count > 1, "a long document should be more than one chunk");

        foreach (var chunk in chunks)
        {
            Assert.True(
                Tokens.Estimate(chunk.Marked) <= RenderingBudget.Extraction.Tokens,
                $"chunk {chunk.Index} was {Tokens.Estimate(chunk.Marked)} tokens");
        }
    }

    [Fact]
    public void A_short_document_is_one_chunk_and_says_so()
    {
        var chunk = Assert.Single(ProseChunking.Chunk(Document(2)));

        Assert.True(chunk.IsOnlyChunk);
        Assert.Equal(0, chunk.Index);
    }

    [Fact]
    public void Nothing_of_the_document_is_lost_between_the_chunks()
    {
        var document = Document(120);
        var chunks = ProseChunking.Chunk(document);

        var rejoined = string.Join(" ", chunks.Select(static chunk => chunk.Text));

        foreach (var block in document.Blocks)
        {
            Assert.Contains(block.Text, rejoined, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A chunk from the middle of a document arrives with no idea what it is part of unless it is
    /// told, and "the limit is 120" means nothing without the section it is under.
    /// </summary>
    [Fact]
    public void A_chunk_carries_the_headings_it_sits_under()
    {
        var chunks = ProseChunking.Chunk(Document(120));
        var later = chunks.Last();

        Assert.NotEmpty(later.Headings);
        Assert.Equal("Expense policy", later.Headings[0]);
        Assert.StartsWith("[Expense policy / Section", later.Marked, StringComparison.Ordinal);
    }

    /// <summary>
    /// A heading is a promise about what comes next. One at the end of a chunk is a promise to
    /// nobody, and the chunk that follows has lost its subject.
    /// </summary>
    [Fact]
    public void No_chunk_ends_on_a_heading()
    {
        foreach (var chunk in ProseChunking.Chunk(Document(200)))
        {
            var lines = chunk.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.False(lines[^1].StartsWith('#'), $"chunk {chunk.Index} ends on a heading");
        }
    }

    /// <summary>
    /// A table too big for one chunk is cut by rows with the header repeated, rather than by
    /// text - which would leave the model a grid missing its right-hand side.
    /// </summary>
    [Fact]
    public void A_table_too_large_for_one_chunk_is_cut_by_rows_and_keeps_its_header()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "Category", "Limit", "Note" } };

        for (var i = 0; i < 600; i++)
        {
            rows.Add([$"category {i}", $"{100 + i} EUR", "a note of some length about this category"]);
        }

        var document = new ProseDocument("limits", [ProseBlock.Table(rows)], "txt");

        var chunks = ProseChunking.Chunk(document);

        Assert.True(chunks.Count > 1);

        foreach (var chunk in chunks)
        {
            Assert.True(Tokens.Estimate(chunk.Marked) <= RenderingBudget.Extraction.Tokens);
            Assert.StartsWith("| Category | Limit | Note |", chunk.Text, StringComparison.Ordinal);
        }

        // Every row is somewhere, once.
        var all = string.Join("\n", chunks.Select(static chunk => chunk.Text));
        Assert.Equal(600, all.Split('\n').Count(static line => line.StartsWith("| category ", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_empty_document_is_no_chunks_at_all()
    {
        Assert.Empty(ProseChunking.Chunk(new ProseDocument("nothing", [], "txt")));
    }

    // ---- The estimate -----------------------------------------------------------------------

    /// <summary>
    /// The estimate exists to keep a budget, so what matters is that it never runs low. A budget
    /// kept by an estimate that under-counts is not a budget, and the failure shows up as a model
    /// that has quietly stopped seeing the end of its prompt.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("one")]
    [InlineData("What may be claimed, and how.")]
    [InlineData("| Category | Limit |\n| --- | --- |\n| Meals | 45 EUR |")]
    [InlineData("2026-07-20,840.50,EuroPython")]
    public void The_estimate_is_at_least_as_large_as_dividing_by_four(string text)
    {
        Assert.True(Tokens.Estimate(text) >= text.Length / 4);
    }

    [Fact]
    public void The_estimate_grows_with_the_text()
    {
        var once = Tokens.Estimate("What may be claimed, and how.");
        var twice = Tokens.Estimate("What may be claimed, and how. What may be claimed, and how.");

        Assert.True(twice > once);
        Assert.True(twice <= once * 2 + 2);
    }
}
