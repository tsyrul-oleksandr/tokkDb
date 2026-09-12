using System.Text.RegularExpressions;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// The naming rule, in one place, for collections and for columns alike.
///
/// This is SC-4's fourth answer, and it is applied to column names as well as collection names
/// on purpose. The old contract trimmed and compared collections one way and columns another -
/// collections ordinally, columns case-insensitively - which is exactly the sort of quiet
/// disagreement §2.2 of the engine plan recorded. One rule, both kinds of name:
///
/// <list type="bullet">
/// <item>a name is trimmed when it is created, so trailing whitespace from a model's output or a
/// spreadsheet header never becomes part of the name;</item>
/// <item>names are then compared <b>ordinally</b>, so <c>Expenses</c> and <c>expenses</c> are two
/// different names. The storage does not decide that two spellings mean one thing. Resolving what
/// a model meant against what already exists is the mapping step's job, which has the schema in
/// front of it and can ask;</item>
/// <item>a name must look like an identifier: a letter, then letters, digits and underscores. The
/// user never sees these names - what they see is the collection's purpose and its metadata - so
/// nothing is lost by keeping them to a shape that needs no escaping wherever they end up.</item>
/// </list>
/// </summary>
internal static partial class StorageNames
{
    public const int MaxLength = 64;

    /// <summary>
    /// Trims and checks a name, or throws <see cref="InvalidDefinitionException"/> saying which
    /// name was wrong and why.
    /// </summary>
    public static string Normalise(string? name, string kind)
    {
        if (name is null)
        {
            throw new InvalidDefinitionException(kind, $"A {kind} is required.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            throw new InvalidDefinitionException(kind, $"A {kind} cannot be blank.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new InvalidDefinitionException(
                kind,
                $"The {kind} '{trimmed}' is {trimmed.Length} characters; the limit is {MaxLength}.");
        }

        if (!NameShape().IsMatch(trimmed))
        {
            throw new InvalidDefinitionException(
                kind,
                $"The {kind} '{trimmed}' is not a usable name. Use a letter, then letters, digits or underscores.");
        }

        return trimmed;
    }

    /// <summary>The comparer every name is compared with, exposed so that no caller picks another.</summary>
    public static StringComparer Comparer => StringComparer.Ordinal;

    public static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameShape();
}
