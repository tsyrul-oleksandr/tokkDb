namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// A document or a text file, converted to something a model can read.
///
/// IN-2, and the same discipline as <see cref="TabularFile"/>: no model is involved, nothing is
/// written anywhere, and the same file gives the same answer every time.
/// </summary>
public static class ProseFile
{
    public static readonly IReadOnlyList<string> Extensions = [".docx", ".docm", ".txt", ".md"];

    public static bool CanRead(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static ProseDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        using var stream = File.OpenRead(path);

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
        {
            return ReadWord(stream, name);
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ReadText(buffer.ToArray(), name);
    }

    public static ProseDocument ReadWord(Stream stream, string name = "document")
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ProseDocument(name, Docx.Read(stream), "docx");
    }

    /// <summary>
    /// A text file. There is no styling to recover, so the structure is what the characters
    /// already say: a blank line ends a block, and a line that begins with a bullet or a number
    /// is a list item.
    ///
    /// Deliberately modest. A text file that is really Markdown will come out as what it already
    /// was, which is right; a text file that is prose comes out as paragraphs, which is also
    /// right. Guessing harder - deciding that a short line in capitals is a heading - would be
    /// inventing structure that is not there, and a wrong heading is worse than no heading
    /// because everything under it inherits the mistake.
    /// </summary>
    public static ProseDocument ReadText(byte[] bytes, string name = "text")
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var (text, encoding, _) = TextEncoding.Decode(bytes);
        var blocks = new List<ProseBlock>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new ProseBlock(ProseBlockKind.Paragraph, string.Join(' ', paragraph)));
            paragraph.Clear();
        }

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            if (Heading(line) is { } heading)
            {
                Flush();
                blocks.Add(heading);
                continue;
            }

            if (ListItem(line) is { } item)
            {
                Flush();
                blocks.Add(item);
                continue;
            }

            paragraph.Add(line.Trim());
        }

        Flush();

        return new ProseDocument(name, blocks, encoding);
    }

    private static ProseBlock? Heading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#')) return null;

        var level = trimmed.TakeWhile(static character => character == '#').Count();
        var text = trimmed[level..].Trim();

        return level is >= 1 and <= 6 && text.Length > 0
            ? new ProseBlock(ProseBlockKind.Heading, text, level)
            : null;
    }

    private static ProseBlock? ListItem(string line)
    {
        var indent = line.Length - line.TrimStart().Length;
        var trimmed = line.Trim();

        if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '•' or '·' && trimmed[1] == ' ')
        {
            return new ProseBlock(ProseBlockKind.ListItem, trimmed[2..].Trim(), indent / 2);
        }

        // "1. " or "1) ", which is how a list arrives in a file somebody typed.
        var digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();

        if (digits is > 0 and < 4
            && trimmed.Length > digits + 1
            && trimmed[digits] is '.' or ')'
            && trimmed[digits + 1] == ' ')
        {
            return new ProseBlock(ProseBlockKind.ListItem, trimmed[(digits + 2)..].Trim(), indent / 2, Ordered: true);
        }

        return null;
    }
}
