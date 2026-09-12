using System.Text;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// What a file's bytes say, when the file does not say.
///
/// Shared by everything that reads one: a CSV file, a text file, anything that arrives as bytes.
/// A byte order mark settles it. Failing that, valid UTF-8 is UTF-8 - the encoding is
/// self-checking enough that a file which decodes cleanly almost certainly is one. Failing that
/// the file is in some single-byte code page and nothing in it says which, so the candidates are
/// scored and the answer is marked as a guess, because the one person who can tell is the person
/// who made the file.
/// </summary>
internal static class TextEncoding
{
    /// <summary>
    /// What the bytes say, or the best guess and a note that it was one.
    ///
    /// A byte order mark settles it. Failing that, valid UTF-8 is UTF-8 - the encoding is
    /// self-checking enough that a file which decodes cleanly almost certainly is one. Failing
    /// that the file is in some single-byte code page and nothing in it says which, so the
    /// candidates are scored on how much of what they produce looks like writing rather than
    /// like symbols and control characters.
    /// </summary>
    public static (string Text, string EncodingName, bool Declared) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8", true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16", true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16BE", true);
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "utf-8", false);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8, so it is one byte per character and the file does not say which set.
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var best = ("", "", double.MinValue);

        foreach (var codePage in new[] { 1252, 1251, 1250 })
        {
            string decoded;
            try
            {
                decoded = Encoding.GetEncoding(codePage).GetString(bytes);
            }
            catch (NotSupportedException)
            {
                continue;
            }

            var score = Readability(decoded);
            if (score > best.Item3) best = (decoded, $"windows-{codePage}", score);
        }

        return best.Item1.Length > 0
            ? (best.Item1, best.Item2, false)
            : (Encoding.Latin1.GetString(bytes), "iso-8859-1", false);
    }

    /// <summary>
    /// How much of a decoded string looks like something somebody wrote. Letters, digits,
    /// spaces and ordinary punctuation count for it; control characters and the symbol-and-
    /// dingbat range a wrong code page produces count against it.
    /// </summary>
    private static double Readability(string text)
    {
        if (text.Length == 0) return 0;

        var good = 0;
        var bad = 0;

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || char.IsPunctuation(character))
            {
                good++;
            }
            else if (char.IsControl(character) || char.IsSymbol(character) || character == '�')
            {
                bad++;
            }
        }

        return (good - 2.0 * bad) / text.Length;
    }

    /// <summary>
    /// Which character separates the fields. The one that gives the most fields per line while
    /// giving the same number on every line: a delimiter that is really a delimiter divides
    /// every row the same way, and one that happens to appear in the text does not.
    /// </summary>
}
