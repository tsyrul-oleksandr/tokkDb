using System.Text;

namespace TokkDb.Assistant.Ingestion;

/// <summary>What a piece of a document is.</summary>
public enum ProseBlockKind
{
    Paragraph = 1,
    Heading,
    ListItem,
    Table,

    /// <summary>A block quotation, which Word keeps as a style rather than as structure.</summary>
    Quote
}

/// <summary>
/// One piece of a document, with what it is kept beside what it says.
///
/// The kind is kept rather than only the marked-up text, for the step after this one. IN-3 has
/// prose reaching a model as chunks of a bounded size, and a chunker that can see the blocks
/// breaks between them; one that can only see a string breaks in the middle of a sentence, or
/// worse, between a heading and the paragraph it introduces.
/// </summary>
public sealed record ProseBlock(
    ProseBlockKind Kind,
    string Text,
    int Level = 0,
    bool Ordered = false,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null)
{
    /// <summary>
    /// The block as text a model can read: a hash for a heading, a dash or a number for a list
    /// item, pipes for a table.
    ///
    /// <b>Markdown, and not for decoration.</b> A model has read more of it than of any other
    /// convention, so a hash is a heading to it without being told; it costs one or two tokens
    /// per block; and it is what the reply phrasing step will want to emit anyway. The
    /// alternative - plain text with the structure thrown away - fails IN-2 outright, because a
    /// heading and a paragraph become the same thing and a table becomes a paragraph of numbers.
    /// </summary>
    public string Marked => Kind switch
    {
        ProseBlockKind.Heading => $"{new string('#', Math.Clamp(Level, 1, 6))} {Text}",
        ProseBlockKind.ListItem => $"{new string(' ', Math.Clamp(Level, 0, 6) * 2)}{(Ordered ? "1." : "-")} {Text}",
        ProseBlockKind.Quote => $"> {Text}",
        _ => Text
    };
}

/// <summary>
/// A document, converted. IN-2's whole surface: a <c>.docx</c> or a <c>.txt</c> in, a form a
/// model can read out, with the headings still headings and the tables still tables.
/// </summary>
public sealed record ProseDocument(string Name, IReadOnlyList<ProseBlock> Blocks, string EncodingName)
{
    /// <summary>The whole document as one piece of text, blocks separated by a blank line.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();

            foreach (var block in Blocks)
            {
                if (text.Length > 0) text.Append('\n').Append('\n');
                text.Append(block.Marked);
            }

            return text.ToString();
        }
    }

    public int HeadingCount => Blocks.Count(static block => block.Kind is ProseBlockKind.Heading);

    public int ListItemCount => Blocks.Count(static block => block.Kind is ProseBlockKind.ListItem);

    public int TableCount => Blocks.Count(static block => block.Kind is ProseBlockKind.Table);
}
