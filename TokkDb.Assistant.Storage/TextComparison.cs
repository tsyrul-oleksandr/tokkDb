using System.Globalization;
using System.Text;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// How two pieces of text are compared: accents dropped, case folded, and only the first
/// <see cref="MaxComparedCharacters"/> characters considered.
///
/// <b>Text is stored exactly and compared loosely.</b> SC-3 is about what is stored - a value
/// goes in as itself and comes back as itself, and nothing here changes that. This is about what
/// counts as the same text when one is being looked for, and it is the engine's rule rather than
/// one of the assistant's: the engine folds string index keys at write time and compares them
/// ordinally afterwards, so that the same query cannot return different answers on two machines
/// with different locales. An implementation that compared text any other way would answer
/// differently from the index the other implementation is seeking, which is the drift §2.2 of
/// the engine plan recorded.
///
/// What it means in practice, and all three are improvements for someone searching their own
/// data rather than costs:
///
/// <list type="bullet">
/// <item>a search for <c>euro</c> finds <c>EuroPython</c>;</item>
/// <item><c>Jose</c> and <c>José</c> are the same text to a query;</item>
/// <item>a unique column treats <c>R-0001</c> and <c>r-0001</c> as the same receipt number.</item>
/// </list>
///
/// <b>The limit worth knowing about</b> is the last one in the list above, taken to its end: two
/// texts that agree for their first 128 characters are the same text to a comparison, because
/// that is where the engine's index key stops. It errs towards finding more records rather than
/// fewer, which is the safe direction for a search, and towards refusing a duplicate that is not
/// one, which is not. It is the engine's constant and is raised rather than worked around, since
/// working around it would mean not using the index that enforces uniqueness in the first place.
/// </summary>
internal static class TextComparison
{
    /// <summary>
    /// Where the engine's string key stops: 256 bytes of UTF-16, which is 128 characters.
    /// </summary>
    public const int MaxComparedCharacters = 128;

    /// <summary>
    /// The form two pieces of text are compared in. Decompose, drop the combining marks,
    /// recompose, upper-case with the invariant culture - invariant so that a Turkish locale
    /// cannot make <c>I</c> and <c>i</c> stop folding - then cut to the length the key has room
    /// for, at a whole character.
    /// </summary>
    public static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var kept = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                kept.Append(character);
            }
        }

        var folded = kept.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();

        return folded.Length <= MaxComparedCharacters ? folded : folded[..MaxComparedCharacters];
    }

    /// <summary>The value a comparison sees. Anything that is not text is compared as itself.</summary>
    public static object? AsCompared(ColumnType type, object? value) =>
        type is ColumnType.Text && value is string text ? Fold(text) : value;
}
