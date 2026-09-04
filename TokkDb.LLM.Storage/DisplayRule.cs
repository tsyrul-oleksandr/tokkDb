using System.Text;

namespace TokkDb.LLM.Storage;

/// <summary>
/// Template describing how a record of a collection is turned into a
/// human-readable display value.
///
/// The syntax is deliberately small and deterministic: column references in
/// braces, literal text everywhere else. It is not an expression language, and
/// evaluating it never calls an LLM.
/// <code>
/// {FullName}
/// {FullName} - {Email}
/// Customer: {FullName}
/// {ProductName} | ${Price}
/// </code>
/// A literal brace is written by doubling it (<c>{{</c> or <c>}}</c>).
/// </summary>
public sealed record DisplayRule
{
    public const int MaxTemplateLength = 512;

    public DisplayRule(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.Length > MaxTemplateLength)
        {
            throw new ArgumentException(
                $"Display rule template exceeds {MaxTemplateLength} characters.",
                nameof(template));
        }

        Template = template;
    }

    public string Template { get; }

    /// <summary>
    /// Creates a rule from user or AI supplied text, returning <c>null</c> for
    /// blank input rather than throwing.
    /// </summary>
    public static DisplayRule? TryCreate(string? template)
    {
        return string.IsNullOrWhiteSpace(template) || template.Length > MaxTemplateLength
            ? null
            : new DisplayRule(template);
    }

    public override string ToString() => Template;
}

/// <summary>
/// A single piece of a parsed template: either literal text or a column reference.
/// </summary>
public sealed record DisplaySegment(string Text, bool IsColumnReference);

/// <summary>
/// Parsed form of a <see cref="DisplayRule"/>.
///
/// Parsing happens once per distinct template and the result is cached by
/// <see cref="DisplayRuleEvaluator"/>, so rendering many records does not
/// re-parse the template.
/// </summary>
public sealed class CompiledDisplayRule
{
    private CompiledDisplayRule(
        string template,
        IReadOnlyList<DisplaySegment> segments,
        IReadOnlyCollection<string> columnReferences,
        string? parseError)
    {
        Template = template;
        Segments = segments;
        ColumnReferences = columnReferences;
        ParseError = parseError;
    }

    public string Template { get; }

    public IReadOnlyList<DisplaySegment> Segments { get; }

    /// <summary>Distinct column names referenced by the template.</summary>
    public IReadOnlyCollection<string> ColumnReferences { get; }

    /// <summary>Non-null when the template could not be parsed.</summary>
    public string? ParseError { get; }

    public bool IsValidSyntax => ParseError is null;

    /// <summary>
    /// Parses a template. Never throws: a malformed template produces a result
    /// carrying <see cref="ParseError"/> so callers can report it.
    /// </summary>
    public static CompiledDisplayRule Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var segments = new List<DisplaySegment>();
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var literal = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            var current = template[i];

            if (current == '{')
            {
                // Doubled brace is an escaped literal.
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    return Failed(template, $"Unclosed '{{' at position {i}.");
                }

                var name = template[(i + 1)..close];
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Failed(template, $"Empty column reference at position {i}.");
                }

                if (name.Contains('{', StringComparison.Ordinal))
                {
                    return Failed(template, $"Nested '{{' inside a column reference at position {i}.");
                }

                var columnName = name.Trim();
                if (!IsValidColumnReference(columnName))
                {
                    return Failed(template, $"Invalid column name '{columnName}'.");
                }

                if (literal.Length > 0)
                {
                    segments.Add(new DisplaySegment(literal.ToString(), false));
                    literal.Clear();
                }

                segments.Add(new DisplaySegment(columnName, true));
                references.Add(columnName);
                i = close;
                continue;
            }

            if (current == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i++;
                    continue;
                }

                return Failed(template, $"Unmatched '}}' at position {i}.");
            }

            literal.Append(current);
        }

        if (literal.Length > 0)
        {
            segments.Add(new DisplaySegment(literal.ToString(), false));
        }

        if (references.Count == 0)
        {
            return Failed(template, "Template does not reference any column.");
        }

        return new CompiledDisplayRule(template, segments, references.ToArray(), null);
    }

    /// <summary>
    /// Rewrites column references, used when a column is renamed. Only actual
    /// references are replaced - literal text is never touched.
    /// </summary>
    public string RewriteColumnReference(string fromColumn, string toColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(toColumn);

        var builder = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (!segment.IsColumnReference)
            {
                // Restore escaping so the rewritten template re-parses identically.
                builder.Append(segment.Text.Replace("{", "{{", StringComparison.Ordinal)
                    .Replace("}", "}}", StringComparison.Ordinal));
                continue;
            }

            var name = string.Equals(segment.Text, fromColumn, StringComparison.OrdinalIgnoreCase)
                ? toColumn
                : segment.Text;

            builder.Append('{').Append(name).Append('}');
        }

        return builder.ToString();
    }

    private static bool IsValidColumnReference(string name)
    {
        return name.All(character =>
            char.IsLetterOrDigit(character) || character == '_' || character == '-');
    }

    private static CompiledDisplayRule Failed(string template, string error) =>
        new(template, Array.Empty<DisplaySegment>(), Array.Empty<string>(), error);
}
