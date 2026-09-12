using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TokkDb.Assistant.Ingestion;
using WordTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WordTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using WordTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// IN-2: a document converted to text in which a heading is still a heading, a list is still a
/// list, and a table is still a table.
///
/// None of the three is marked as such in the file - a heading is a style name, a list is a
/// reference to a numbering definition in another part of the package, and a table is rows of
/// cells of paragraphs - so each of them is a thing that can be silently lost, and each has a
/// test that would notice.
///
/// The documents are built here rather than checked in, so that what is being read is in the
/// test rather than in a binary somebody has to open.
/// </summary>
public sealed class ProseTests
{
    /// <summary>
    /// The requirement's own case, end to end: headings, a bulleted list and a two-column table,
    /// all three still apparent afterwards.
    /// </summary>
    [Fact]
    public void A_document_with_headings_a_list_and_a_table_keeps_all_three()
    {
        var document = ProseFile.ReadWord(Word.Build(body =>
        {
            body.Heading("Expense policy", 1);
            body.Paragraph("What may be claimed, and how.");
            body.Heading("What is covered", 2);
            body.Bullet("Conference tickets");
            body.Bullet("Travel, second class");
            body.Bullet("One night's accommodation per travel day");
            body.Heading("Limits", 2);
            body.Table(
                ["Category", "Limit"],
                ["Accommodation", "120 EUR"],
                ["Meals", "45 EUR"]);
        }));

        // The three kinds survived as kinds, not only as text.
        Assert.Equal(3, document.HeadingCount);
        Assert.Equal(3, document.ListItemCount);
        Assert.Equal(1, document.TableCount);

        var text = document.Text;

        // And they are apparent in the text a model would be given.
        Assert.Contains("# Expense policy", text, StringComparison.Ordinal);
        Assert.Contains("## What is covered", text, StringComparison.Ordinal);
        Assert.Contains("- Conference tickets", text, StringComparison.Ordinal);
        Assert.Contains("| Category | Limit |", text, StringComparison.Ordinal);
        Assert.Contains("| --- | --- |", text, StringComparison.Ordinal);
        Assert.Contains("| Meals | 45 EUR |", text, StringComparison.Ordinal);

        // The heading level is the one the document gave, not a guess: the second-level headings
        // are under the first-level one and stay under it.
        var headings = document.Blocks
            .Where(static block => block.Kind is ProseBlockKind.Heading)
            .Select(static block => (block.Text, block.Level))
            .ToArray();

        Assert.Equal([("Expense policy", 1), ("What is covered", 2), ("Limits", 2)], headings);

        // The paragraph is a paragraph and carries no marking of its own.
        var paragraph = Assert.Single(document.Blocks, static b => b.Kind is ProseBlockKind.Paragraph);
        Assert.Equal("What may be claimed, and how.", paragraph.Marked);
    }

    [Fact]
    public void A_numbered_list_is_told_apart_from_a_bulleted_one()
    {
        var document = ProseFile.ReadWord(Word.Build(body =>
        {
            body.Bullet("A bulleted thing");
            body.Numbered("A numbered thing");
            body.Numbered("Another numbered thing");
        }));

        var items = document.Blocks.Where(static block => block.Kind is ProseBlockKind.ListItem).ToArray();

        Assert.Equal(3, items.Length);
        Assert.False(items[0].Ordered);
        Assert.True(items[1].Ordered);
        Assert.True(items[2].Ordered);

        Assert.Equal("- A bulleted thing", items[0].Marked);
        Assert.Equal("1. A numbered thing", items[1].Marked);
    }

    [Fact]
    public void A_nested_list_keeps_its_levels()
    {
        var document = ProseFile.ReadWord(Word.Build(body =>
        {
            body.Bullet("Travel");
            body.Bullet("Second class", level: 1);
            body.Bullet("Booked in advance", level: 2);
        }));

        var items = document.Blocks.Where(static block => block.Kind is ProseBlockKind.ListItem).ToArray();

        Assert.Equal([0, 1, 2], items.Select(static item => item.Level));
        Assert.Equal("  - Second class", items[1].Marked);
        Assert.Equal("    - Booked in advance", items[2].Marked);
    }

    /// <summary>
    /// A table's cells are kept as cells as well as rendered, so that the step after this one can
    /// bound a large table by rows rather than by cutting its text in half.
    /// </summary>
    [Fact]
    public void A_table_keeps_its_cells_as_well_as_its_text()
    {
        var document = ProseFile.ReadWord(Word.Build(body =>
            body.Table(["Category", "Limit"], ["Meals", "45 EUR"])));

        var table = Assert.Single(document.Blocks, static block => block.Kind is ProseBlockKind.Table);

        Assert.NotNull(table.Rows);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Category", "Limit"], table.Rows[0]);
        Assert.Equal(["Meals", "45 EUR"], table.Rows[1]);
    }

    /// <summary>
    /// A cell holding a pipe would add a column to its row, and the table would stop lining up
    /// from there down - which a model reads as a differently shaped table rather than as a
    /// mistake.
    /// </summary>
    [Fact]
    public void A_cell_holding_a_pipe_does_not_add_a_column()
    {
        var document = ProseFile.ReadWord(Word.Build(body =>
            body.Table(["Category", "Note"], ["Meals", "breakfast | dinner"])));

        var table = Assert.Single(document.Blocks, static block => block.Kind is ProseBlockKind.Table);

        Assert.Contains(@"breakfast \| dinner", table.Text, StringComparison.Ordinal);
        Assert.Equal(["Meals", "breakfast | dinner"], table.Rows![1]);
    }

    [Fact]
    public void An_empty_paragraph_is_not_a_block()
    {
        var document = ProseFile.ReadWord(Word.Build(body =>
        {
            body.Paragraph("Something.");
            body.Paragraph("");
            body.Paragraph("   ");
            body.Paragraph("Something else.");
        }));

        Assert.Equal(2, document.Blocks.Count);
    }

    // ---- Text files -------------------------------------------------------------------------

    [Fact]
    public void A_text_file_keeps_the_structure_its_characters_already_have()
    {
        var document = ProseFile.ReadText(Encoding.UTF8.GetBytes(
            """
            # Expense policy

            What may be claimed, and how.
            The second line of the same paragraph.

            - Conference tickets
            - Travel, second class

            1. Ask first
            2. Then claim
            """));

        Assert.Equal(1, document.HeadingCount);
        Assert.Equal(4, document.ListItemCount);

        var paragraph = Assert.Single(document.Blocks, static b => b.Kind is ProseBlockKind.Paragraph);
        Assert.Equal("What may be claimed, and how. The second line of the same paragraph.", paragraph.Text);

        var items = document.Blocks.Where(static b => b.Kind is ProseBlockKind.ListItem).ToArray();
        Assert.False(items[0].Ordered);
        Assert.True(items[2].Ordered);
        Assert.Equal("Ask first", items[2].Text);
    }

    [Fact]
    public void A_text_file_that_is_only_prose_comes_out_as_paragraphs()
    {
        var document = ProseFile.ReadText(Encoding.UTF8.GetBytes(
            "The first thing.\n\nThe second thing, which\nruns over two lines.\n"));

        Assert.Equal(2, document.Blocks.Count);
        Assert.All(document.Blocks, static block => Assert.Equal(ProseBlockKind.Paragraph, block.Kind));
        Assert.Equal("The second thing, which runs over two lines.", document.Blocks[1].Text);
    }

    [Fact]
    public void A_text_file_is_decoded_the_same_way_a_data_file_is()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1251).GetBytes("Політика витрат\n\nЩо можна відшкодувати.");

        var document = ProseFile.ReadText(bytes);

        Assert.Equal("windows-1251", document.EncodingName);
        Assert.Equal("Політика витрат", document.Blocks[0].Text);
    }

    // ---- The ugly file R-5 asks for ---------------------------------------------------------

    /// <summary>
    /// A document assembled from a template wraps its parts in content controls. A reader that
    /// only looked at the body's own children would return nothing for such a document - not an
    /// error, an empty document, which is the worst way to fail.
    /// </summary>
    [Fact]
    public void Content_inside_a_content_control_is_still_content()
    {
        var document = ProseFile.ReadWord(Word.BuildRaw(body =>
        {
            body.Append(new Paragraph(new Run(new Text("Before the control."))));

            body.Append(new SdtBlock(new SdtContentBlock(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("Inside the control"))),
                new Paragraph(new Run(new Text("Also inside."))))));
        }));

        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal(1, document.HeadingCount);
        Assert.Equal("Inside the control", document.Blocks[1].Text);
    }

    /// <summary>
    /// A document styled by hand rather than with the built-in styles says a paragraph is a
    /// heading through its outline level instead, and Word writes the first level as zero.
    /// </summary>
    [Fact]
    public void A_heading_marked_only_by_its_outline_level_is_still_a_heading()
    {
        var document = ProseFile.ReadWord(Word.BuildRaw(body =>
            body.Append(new Paragraph(
                new ParagraphProperties(new OutlineLevel { Val = 0 }),
                new Run(new Text("Styled by hand"))))));

        var heading = Assert.Single(document.Blocks);
        Assert.Equal(ProseBlockKind.Heading, heading.Kind);
        Assert.Equal(1, heading.Level);
        Assert.Equal("# Styled by hand", heading.Marked);
    }

    /// <summary>
    /// Text inside a rejected tracked change is not in the document as it stands. Including it
    /// would put a sentence the author deleted in front of the model as though they had written
    /// it, which is the kind of mistake nobody notices until it matters.
    /// </summary>
    [Fact]
    public void Text_that_was_deleted_in_a_tracked_change_is_not_in_the_document()
    {
        var document = ProseFile.ReadWord(Word.BuildRaw(body =>
            body.Append(new Paragraph(
                new Run(new Text("What is kept. ") { Space = SpaceProcessingModeValues.Preserve }),
                new DeletedRun(new Run(new DeletedText("What was struck out."))),
                new Run(new Text("And the rest.") { Space = SpaceProcessingModeValues.Preserve })))));

        var paragraph = Assert.Single(document.Blocks);
        Assert.Equal("What is kept. And the rest.", paragraph.Text);
        Assert.DoesNotContain("struck out", paragraph.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_inside_a_cell_is_flattened_into_it_rather_than_lost()
    {
        var document = ProseFile.ReadWord(Word.BuildRaw(body =>
        {
            var inner = new WordTable(
                new WordTableRow(
                    new WordTableCell(new Paragraph(new Run(new Text("breakfast")))),
                    new WordTableCell(new Paragraph(new Run(new Text("6 EUR"))))));

            body.Append(new WordTable(
                new WordTableRow(
                    new WordTableCell(new Paragraph(new Run(new Text("Category")))),
                    new WordTableCell(new Paragraph(new Run(new Text("Detail"))))),
                new WordTableRow(
                    new WordTableCell(new Paragraph(new Run(new Text("Meals")))),
                    new WordTableCell(inner, new Paragraph()))));
        }));

        var table = Assert.Single(document.Blocks, static block => block.Kind is ProseBlockKind.Table);

        Assert.Equal(2, table.Rows!.Count);
        Assert.Equal("breakfast, 6 EUR", table.Rows[1][1]);
    }

    /// <summary>Building the documents the tests above read.</summary>
    private static class Word
    {
        private const int BulletNumbering = 1;
        private const int OrderedNumbering = 2;

        /// <summary>The same package, with the body built element by element.</summary>
        public static Stream BuildRaw(Action<Body> write) => Build(builder => write(builder.Body));

        public static Stream Build(Action<BodyBuilder> write)
        {
            var stream = new MemoryStream();

            using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
            {
                var main = document.AddMainDocumentPart();
                var body = new Body();
                main.Document = new Document(body);

                // One bulleted definition and one numbered one, reached through the same
                // indirection Word uses: an instance points at an abstract definition, and the
                // format is on the level of that.
                var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                numbering.Numbering = new Numbering(
                    Abstract(0, NumberFormatValues.Bullet),
                    Abstract(1, NumberFormatValues.Decimal),
                    new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = BulletNumbering },
                    new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = OrderedNumbering });

                write(new BodyBuilder(body));
            }

            stream.Position = 0;
            return stream;
        }

        private static AbstractNum Abstract(int id, NumberFormatValues format)
        {
            var definition = new AbstractNum { AbstractNumberId = id };

            for (var level = 0; level < 3; level++)
            {
                definition.Append(new Level(new NumberingFormat { Val = format }) { LevelIndex = level });
            }

            return definition;
        }

        internal sealed class BodyBuilder(Body body)
        {
            public Body Body => body;

            public void Heading(string text, int level) =>
                body.Append(Styled(text, $"Heading{level}"));

            public void Paragraph(string text) => body.Append(Styled(text, null));

            public void Bullet(string text, int level = 0) => body.Append(Listed(text, BulletNumbering, level));

            public void Numbered(string text, int level = 0) => body.Append(Listed(text, OrderedNumbering, level));

            public void Table(params string[][] rows)
            {
                var table = new WordTable();

                foreach (var row in rows)
                {
                    var built = new WordTableRow();
                    foreach (var cell in row) built.Append(new WordTableCell(Styled(cell, null)));
                    table.Append(built);
                }

                body.Append(table);
            }

            private static Paragraph Styled(string text, string? style)
            {
                var paragraph = new Paragraph();

                if (style is not null)
                {
                    paragraph.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
                }

                paragraph.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                return paragraph;
            }

            private static Paragraph Listed(string text, int numberingId, int level) =>
                new(
                    new ParagraphProperties(new NumberingProperties(
                        new NumberingLevelReference { Val = level },
                        new NumberingId { Val = numberingId })),
                    new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }
    }
}
