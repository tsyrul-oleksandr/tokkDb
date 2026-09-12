using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

// Table and TableRow are names this assembly already uses for the tabular side, and the
// namespace this file is in wins over the one it imports. Aliased rather than renamed, because
// the spreadsheet meaning of "table" is the one the rest of the assembly is about.
using WordTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WordTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using WordTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// Reading a Word document down to the three things IN-2 asks be kept: what is a heading, what is
/// a list, and what is a table.
///
/// None of the three is marked as such in the file, which is the cost R-5 named and the second
/// half of it:
///
/// <list type="number">
/// <item><b>A heading is a style name.</b> There is no heading element - there is a paragraph
/// whose style is called <c>Heading2</c>, and the level is the digit on the end of the name. A
/// document that was styled by hand instead says so through an outline level, which is read as
/// well.</item>
/// <item><b>A list is a reference to a numbering definition.</b> The paragraph says "I am at
/// level 1 of numbering 3"; whether that is a bullet or a number is in another part of the
/// package, so the numbering definitions have to be read and followed through an indirection -
/// the instance points at an abstract definition, and the format is on the level of that.</item>
/// <item><b>A table is rows of cells of paragraphs.</b> Cells hold whole documents, so reading
/// one means reading its paragraphs, and a table inside a cell means doing it again.</item>
/// </list>
/// </summary>
internal static class Docx
{
    /// <summary>How deep a nested table is followed before it is left as text.</summary>
    private const int MaxTableDepth = 3;

    public static IReadOnlyList<ProseBlock> Read(Stream stream)
    {
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var main = document.MainDocumentPart
            ?? throw new InvalidDataException("The file is not a Word document.");

        var body = main.Document?.Body;
        if (body is null) return [];

        var numbering = ListFormats(main);
        var blocks = new List<ProseBlock>();

        foreach (var element in Contents(body))
        {
            switch (element)
            {
                case Paragraph paragraph:
                    if (FromParagraph(paragraph, numbering) is { } block) blocks.Add(block);
                    break;

                case WordTable table:
                    blocks.Add(FromTable(table, numbering, depth: 1));
                    break;
            }
        }

        return blocks;
    }

    /// <summary>
    /// The body's contents, with content controls looked through.
    ///
    /// A document assembled from a template wraps its parts in structured document tags, and the
    /// paragraphs inside one are paragraphs. A reader that only looked at the body's direct
    /// children would silently return nothing for such a document, which is worse than failing.
    /// </summary>
    private static IEnumerable<OpenXmlElement> Contents(OpenXmlElement parent)
    {
        foreach (var child in parent.ChildElements)
        {
            switch (child)
            {
                case Paragraph or WordTable:
                    yield return child;
                    break;

                case SdtBlock or SdtContentBlock:
                    foreach (var inner in Contents(child)) yield return inner;
                    break;

                default:
                    // A section break, a bookmark, a proof error: structure that carries no text.
                    if (child.HasChildren && child is not BookmarkStart and not BookmarkEnd)
                    {
                        foreach (var inner in Contents(child)) yield return inner;
                    }

                    break;
            }
        }
    }

    private static ProseBlock? FromParagraph(Paragraph paragraph, IReadOnlyDictionary<int, bool> numbering)
    {
        var text = TextOf(paragraph);
        if (text.Length == 0) return null;

        var properties = paragraph.ParagraphProperties;
        var style = properties?.ParagraphStyleId?.Val?.Value ?? "";

        // A list first: a numbered paragraph may also carry a heading style, and what the reader
        // wants is the list item.
        if (properties?.NumberingProperties is { } list)
        {
            var level = list.NumberingLevelReference?.Val?.Value ?? 0;
            var ordered = list.NumberingId?.Val?.Value is { } id && numbering.TryGetValue(id, out var isOrdered)
                ? isOrdered
                : false;

            return new ProseBlock(ProseBlockKind.ListItem, text, level, ordered);
        }

        if (HeadingLevel(style, properties?.OutlineLevel?.Val?.Value) is { } heading)
        {
            return new ProseBlock(ProseBlockKind.Heading, text, heading);
        }

        if (style.Contains("Quote", StringComparison.OrdinalIgnoreCase))
        {
            return new ProseBlock(ProseBlockKind.Quote, text);
        }

        // A list styled by hand rather than numbered. The style is the only thing that says so,
        // and losing it would turn a list into a run of one-line paragraphs.
        if (style.StartsWith("ListParagraph", StringComparison.OrdinalIgnoreCase))
        {
            return new ProseBlock(ProseBlockKind.ListItem, text);
        }

        return new ProseBlock(ProseBlockKind.Paragraph, text);
    }

    /// <summary>
    /// The level of a heading, or null if the paragraph is not one. <c>Heading2</c> is level two;
    /// a title is level one; an outline level says the same thing zero-based.
    /// </summary>
    private static int? HeadingLevel(string style, int? outlineLevel)
    {
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var digits = style["Heading".Length..].TrimStart(' ', '-');
            return int.TryParse(digits, out var level) ? Math.Clamp(level, 1, 6) : 1;
        }

        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        if (style.Equals("Subtitle", StringComparison.OrdinalIgnoreCase)) return 2;

        // Word writes the outline level as zero for a first-level heading.
        return outlineLevel is >= 0 and <= 8 ? outlineLevel + 1 : null;
    }

    private static ProseBlock FromTable(WordTable table, IReadOnlyDictionary<int, bool> numbering, int depth)
    {
        var rows = new List<IReadOnlyList<string>>();

        foreach (var row in table.Elements<WordTableRow>())
        {
            var cells = new List<string>();

            foreach (var cell in row.Elements<WordTableCell>())
            {
                var pieces = new List<string>();

                foreach (var element in Contents(cell))
                {
                    switch (element)
                    {
                        case Paragraph paragraph when TextOf(paragraph) is { Length: > 0 } text:
                            pieces.Add(text);
                            break;

                        // A table inside a cell, flattened into it. Rendering a second grid
                        // inside one cell of the first would produce something no reader could
                        // follow, so its rows are joined instead.
                        case WordTable nested when depth < MaxTableDepth:
                            pieces.Add(string.Join("; ",
                                FromTable(nested, numbering, depth + 1).Rows!.Select(
                                    static inner => string.Join(", ", inner))));
                            break;
                    }
                }

                cells.Add(string.Join(" ", pieces));
            }

            if (cells.Count > 0) rows.Add(cells);
        }

        return new ProseBlock(ProseBlockKind.Table, Render(rows), Rows: rows);
    }

    /// <summary>
    /// A table as a model reads one: pipes, and a rule under the first row so that the row is
    /// read as the names of the columns. Every cell's own pipes are escaped, or one cell holding
    /// a pipe would add a column to that row and the table would stop lining up.
    /// </summary>
    private static string Render(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return "";

        var width = rows.Max(static row => row.Count);
        var text = new StringBuilder();

        for (var r = 0; r < rows.Count; r++)
        {
            text.Append("| ");

            for (var c = 0; c < width; c++)
            {
                var cell = c < rows[r].Count ? rows[r][c] : "";
                text.Append(cell.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' '));
                text.Append(" |");
                if (c < width - 1) text.Append(' ');
            }

            text.Append('\n');

            if (r == 0)
            {
                text.Append('|');
                for (var c = 0; c < width; c++) text.Append(" --- |");
                text.Append('\n');
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The text of a paragraph, runs joined. A tab becomes a space and a line break becomes one
    /// too, because a block is a block and the shape inside it is the author's typography rather
    /// than their meaning.
    /// </summary>
    private static string TextOf(OpenXmlElement paragraph)
    {
        var text = new StringBuilder();

        foreach (var run in paragraph.Descendants<Run>())
        {
            // Deleted text belongs to a tracked change that was rejected; it is not in the
            // document as it stands.
            if (run.Ancestors<DeletedRun>().Any()) continue;

            foreach (var piece in run.ChildElements)
            {
                switch (piece)
                {
                    case Text value:
                        text.Append(value.Text);
                        break;

                    case TabChar:
                    case Break:
                        text.Append(' ');
                        break;

                    case NoBreakHyphen:
                        text.Append('-');
                        break;
                }
            }
        }

        return string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Which numbering definitions are ordered rather than bulleted, by the id a paragraph
    /// refers to.
    ///
    /// Two hops: the paragraph names an instance, the instance names an abstract definition, and
    /// the format is on the levels of that. The first level's format is taken as the list's,
    /// because a list that is bullets at one level and numbers at another is a document doing
    /// something a plain-text rendering cannot represent anyway.
    /// </summary>
    private static Dictionary<int, bool> ListFormats(MainDocumentPart main)
    {
        var ordered = new Dictionary<int, bool>();
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return ordered;

        var abstracts = new Dictionary<int, bool>();

        foreach (var definition in numbering.Elements<AbstractNum>())
        {
            if (definition.AbstractNumberId?.Value is not { } id) continue;

            var first = definition.Elements<Level>()
                .OrderBy(static level => level.LevelIndex?.Value ?? 0)
                .FirstOrDefault();

            var format = first?.NumberingFormat?.Val?.Value;

            abstracts[id] = format is not null
                            && format != NumberFormatValues.Bullet
                            && format != NumberFormatValues.None;
        }

        foreach (var instance in numbering.Elements<NumberingInstance>())
        {
            if (instance.NumberID?.Value is not { } id) continue;
            if (instance.AbstractNumId?.Val?.Value is not { } abstractId) continue;

            ordered[id] = abstracts.GetValueOrDefault(abstractId);
        }

        return ordered;
    }
}
