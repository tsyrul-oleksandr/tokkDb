using System.Text;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// One piece of a document, small enough to send.
///
/// The headings are carried separately and repeated at the top of the piece. A chunk torn out of
/// the middle of a document otherwise arrives with no idea what it is part of, and the model is
/// left to infer from the prose whether "the limit is 120" is about accommodation or about
/// mileage. Three or four tokens of heading is the cheapest context there is.
/// </summary>
public sealed record ProseChunk(int Index, int Count, IReadOnlyList<string> Headings, string Text)
{
    /// <summary>The chunk as it would be sent: where it is from, then what it says.</summary>
    public string Marked => Headings.Count == 0
        ? Text
        : $"[{string.Join(" / ", Headings)}]\n{Text}";

    public bool IsOnlyChunk => Count == 1;
}

/// <summary>
/// IN-3's second half: prose cut to a budget.
///
/// Where a table is <i>described</i> because its rows repeat themselves, prose has to be
/// <i>divided</i>, because every part of it says something different and none of it can be
/// summarised without a model - which is what it is being sent to in the first place.
///
/// Three rules, in order of how much they matter:
///
/// <list type="number">
/// <item><b>Break between blocks.</b> A chunk that ends mid-sentence costs the model the sentence
/// and often the paragraph. Blocks are the natural seams and the reason <see cref="ProseBlock"/>
/// keeps its kind.</item>
/// <item><b>Never end a chunk on a heading.</b> A heading is a promise about what comes next, so
/// one at the end of a chunk is a promise to nobody and a chunk starting under it has lost its
/// subject. A heading with nothing under it moves to the next chunk.</item>
/// <item><b>A block too big for a budget is split, and a table is split by rows.</b> Cutting a
/// table's text in half leaves a model with a grid missing its right-hand side; cutting it by
/// rows and repeating the header leaves two smaller tables, which is what it is.</item>
/// </list>
/// </summary>
public static class ProseChunking
{
    public static IReadOnlyList<ProseChunk> Chunk(ProseDocument document) =>
        Chunk(document, RenderingBudget.Extraction);

    public static IReadOnlyList<ProseChunk> Chunk(ProseDocument document, RenderingBudget budget)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(budget);

        var chunks = new List<(List<string> Headings, StringBuilder Text)>();
        var headings = new List<string>();
        var current = new StringBuilder();
        var currentHeadings = new List<string>();
        var pendingHeadings = 0;

        void Close()
        {
            if (current.Length == 0) return;

            // A chunk that is nothing but headings is a promise with nothing behind it; its
            // headings stay on the stack and lead the next chunk instead.
            if (pendingHeadings > 0 && OnlyHeadings(current.ToString()))
            {
                current.Clear();
                pendingHeadings = 0;
                return;
            }

            chunks.Add(([.. currentHeadings], new StringBuilder(current.ToString().TrimEnd('\n'))));
            current.Clear();
            pendingHeadings = 0;
        }

        void Open()
        {
            currentHeadings = [.. headings];
        }

        Open();

        foreach (var block in document.Blocks)
        {
            if (block.Kind is ProseBlockKind.Heading)
            {
                // The stack of headings this chunk sits under: a level-two heading replaces the
                // level-two and everything below it.
                var level = Math.Clamp(block.Level, 1, 6);
                while (headings.Count >= level) headings.RemoveAt(headings.Count - 1);
                while (headings.Count < level - 1) headings.Add("");
                headings.Add(block.Text);
            }

            foreach (var piece in Divide(block, budget, currentHeadings))
            {
                var addition = current.Length == 0 ? piece : "\n\n" + piece;

                if (current.Length > 0 && Cost(currentHeadings, current + addition) > budget.Tokens)
                {
                    Close();
                    Open();
                    current.Append(piece);
                }
                else
                {
                    current.Append(addition);
                }

                if (block.Kind is ProseBlockKind.Heading) pendingHeadings++;
                else pendingHeadings = 0;
            }
        }

        Close();

        return [.. chunks.Select((chunk, index) =>
            new ProseChunk(index, chunks.Count, chunk.Headings, chunk.Text.ToString()))];
    }

    private static bool OnlyHeadings(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(static line => line.StartsWith('#'));

    private static int Cost(IReadOnlyList<string> headings, string text) =>
        Tokens.Estimate(string.Join(" / ", headings)) + Tokens.Estimate(text);

    /// <summary>
    /// A block, or the pieces of one that is too big to send whole.
    /// </summary>
    private static IEnumerable<string> Divide(
        ProseBlock block,
        RenderingBudget budget,
        IReadOnlyList<string> headings)
    {
        var marked = block.Marked;
        var room = budget.Tokens - Tokens.Estimate(string.Join(" / ", headings));

        if (Tokens.Estimate(marked) <= room)
        {
            yield return marked;
            yield break;
        }

        if (block.Kind is ProseBlockKind.Table && block.Rows is { Count: > 1 })
        {
            foreach (var piece in ByRows(block.Rows, room)) yield return piece;
            yield break;
        }

        foreach (var piece in BySentences(marked, room)) yield return piece;
    }

    /// <summary>
    /// A table split by rows, each piece carrying the header again. Two tables of the same shape
    /// rather than one table with its right-hand side missing.
    /// </summary>
    private static IEnumerable<string> ByRows(IReadOnlyList<IReadOnlyList<string>> rows, int room)
    {
        var width = rows.Max(static row => row.Count);
        var headerText = ProseBlock.Row(rows[0], width)
                         + "\n|" + string.Concat(Enumerable.Repeat(" --- |", width));
        var headerCost = Tokens.Estimate(headerText);

        var piece = new StringBuilder(headerText);
        var any = false;

        foreach (var row in rows.Skip(1))
        {
            var line = "\n" + ProseBlock.Row(row, width);

            if (any && headerCost + Tokens.Estimate(piece + line) > room)
            {
                yield return piece.ToString();
                piece = new StringBuilder(headerText);
                any = false;
            }

            piece.Append(line);
            any = true;
        }

        if (any) yield return piece.ToString();
    }

    /// <summary>
    /// A block of prose split at the ends of sentences, and at spaces when a sentence is itself
    /// too long. A word is never cut, because half a word is not a word and the model would
    /// read two of them.
    /// </summary>
    private static IEnumerable<string> BySentences(string text, int room)
    {
        var piece = new StringBuilder();

        foreach (var sentence in Sentences(text))
        {
            foreach (var part in sentence.Length > 0 && Tokens.Estimate(sentence) > room
                         ? ByWords(sentence, room)
                         : [sentence])
            {
                var addition = piece.Length == 0 ? part : " " + part;

                if (piece.Length > 0 && Tokens.Estimate(piece + addition) > room)
                {
                    yield return piece.ToString();
                    piece.Clear();
                    piece.Append(part);
                    continue;
                }

                piece.Append(addition);
            }
        }

        if (piece.Length > 0) yield return piece.ToString();
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '\n')) continue;

            // The end of a sentence is the punctuation and whatever space follows it.
            var end = i + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;

            var sentence = text[start..end].Trim();
            if (sentence.Length > 0) yield return sentence;

            start = end;
            i = end - 1;
        }

        var last = text[start..].Trim();
        if (last.Length > 0) yield return last;
    }

    private static IEnumerable<string> ByWords(string sentence, int room)
    {
        var piece = new StringBuilder();

        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var addition = piece.Length == 0 ? word : " " + word;

            if (piece.Length > 0 && Tokens.Estimate(piece + addition) > room)
            {
                yield return piece.ToString();
                piece.Clear();
                piece.Append(word);
                continue;
            }

            piece.Append(addition);
        }

        if (piece.Length > 0) yield return piece.ToString();
    }
}
