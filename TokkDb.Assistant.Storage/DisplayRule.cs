using System.Text;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// How one record of a collection reads as a single line of text.
///
/// SC-2 makes this part of the collection's definition rather than a property of a record, so
/// the line is computed when it is needed and never stored: renaming a column changes every
/// record's display line at once, and no record can fall out of step with the rule.
///
/// The syntax is a template: column names in braces, literal text everywhere else, a doubled
/// brace for a literal one. It is not an expression language and evaluating it never involves a
/// model.
/// <code>
/// {title}
/// {event} in {city}
/// {amount_eur} EUR, {paid_on}
/// </code>
/// </summary>
public sealed record DisplayRule
{
    public const int MaxTemplateLength = 512;

    /// <summary>
    /// Parses <paramref name="template"/>, or throws <see cref="InvalidDefinitionException"/>.
    /// A rule that exists is a rule that parses; whether the columns it names exist is checked
    /// by <see cref="IStorage.CreateCollection"/>, which can see them.
    /// </summary>
    public DisplayRule(string template)
    {
        if (template is null)
        {
            throw new InvalidDefinitionException("display rule", "A display rule is required.");
        }

        if (template.Length > MaxTemplateLength)
        {
            throw new InvalidDefinitionException(
                "display rule",
                $"The display rule is {template.Length} characters; the limit is {MaxTemplateLength}.");
        }

        Template = template;
        (Segments, ColumnReferences) = Parse(template);
    }

    public string Template { get; }

    /// <summary>The template taken apart: literal text and column references, in order.</summary>
    public IReadOnlyList<DisplaySegment> Segments { get; }

    /// <summary>The distinct column names the template names, in the order they first appear.</summary>
    public IReadOnlyList<string> ColumnReferences { get; }

    /// <summary>Returns null for blank or unusable input instead of throwing, for the model-facing path.</summary>
    public static DisplayRule? TryCreate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        try
        {
            return new DisplayRule(template);
        }
        catch (InvalidDefinitionException)
        {
            return null;
        }
    }

    /// <summary>
    /// The same rule with one column reference renamed. Literal text that happens to contain the
    /// old name is left alone, which is why the template is kept in pieces.
    /// </summary>
    public DisplayRule WithColumnRenamed(string from, string to)
    {
        var fromName = StorageNames.Normalise(from, "column name");
        var toName = StorageNames.Normalise(to, "column name");

        var rebuilt = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (segment.IsColumnReference)
            {
                rebuilt.Append('{')
                    .Append(StorageNames.Same(segment.Text, fromName) ? toName : segment.Text)
                    .Append('}');
                continue;
            }

            rebuilt.Append(segment.Text
                .Replace("{", "{{", StringComparison.Ordinal)
                .Replace("}", "}}", StringComparison.Ordinal));
        }

        return new DisplayRule(rebuilt.ToString());
    }

    /// <summary>
    /// Two rules are the same rule when they are the same template, compared ordinally. The
    /// synthesised equality a record would give would compare <see cref="Segments"/> by
    /// reference and report two rules built from one string as different, which is worse than
    /// having no equality at all: it would be wrong rather than absent.
    /// </summary>
    public bool Equals(DisplayRule? other) =>
        other is not null && string.Equals(Template, other.Template, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Template);

    public override string ToString() => Template;

    private static (IReadOnlyList<DisplaySegment> Segments, IReadOnlyList<string> References) Parse(string template)
    {
        var segments = new List<DisplaySegment>();
        var references = new List<string>();
        var literal = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            var character = template[i];

            if (character == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new InvalidDefinitionException(
                        "display rule",
                        $"The display rule has a '{{' at position {i} that is never closed.");
                }

                // Normalise throws with the column-name wording, which is the right complaint:
                // what is between the braces has to be a column name.
                var name = StorageNames.Normalise(template[(i + 1)..close], "column name");

                if (literal.Length > 0)
                {
                    segments.Add(new DisplaySegment(literal.ToString(), false));
                    literal.Clear();
                }

                segments.Add(new DisplaySegment(name, true));
                if (!references.Contains(name, StorageNames.Comparer)) references.Add(name);

                i = close;
                continue;
            }

            if (character == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i++;
                    continue;
                }

                throw new InvalidDefinitionException(
                    "display rule",
                    $"The display rule has a '}}' at position {i} that closes nothing.");
            }

            literal.Append(character);
        }

        if (literal.Length > 0)
        {
            segments.Add(new DisplaySegment(literal.ToString(), false));
        }

        if (references.Count == 0)
        {
            throw new InvalidDefinitionException(
                "display rule",
                "The display rule names no column, so every record would read the same.");
        }

        return (segments, references);
    }
}

/// <summary>One piece of a parsed <see cref="DisplayRule"/>: literal text, or a column name.</summary>
public sealed record DisplaySegment(string Text, bool IsColumnReference);
