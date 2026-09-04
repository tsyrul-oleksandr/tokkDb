using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text;

namespace TokkDb.LLM.Storage;

public interface IDisplayRuleEvaluator
{
    /// <summary>
    /// Renders a record using the rule. Deterministic and side-effect free; it
    /// never calls an LLM and never throws for bad data.
    /// </summary>
    string Evaluate(DisplayRule rule, IReadOnlyDictionary<string, object?> record);

    /// <summary>
    /// Overload carrying collection context so missing columns can be reported
    /// against a named collection.
    /// </summary>
    string Evaluate(
        DisplayRule rule,
        IReadOnlyDictionary<string, object?> record,
        string? collectionName);

    /// <summary>Parsed template, cached per distinct template string.</summary>
    CompiledDisplayRule Compile(DisplayRule rule);
}

/// <inheritdoc />
public sealed class DisplayRuleEvaluator : IDisplayRuleEvaluator
{
    /// <summary>
    /// Templates are parsed once and reused. Evaluation runs per record in the
    /// UI, so re-parsing every time would be wasteful; the cache is bounded by
    /// the number of distinct templates, which is at most one per collection.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CompiledDisplayRule> Cache =
        new(StringComparer.Ordinal);

    private readonly ILogger<DisplayRuleEvaluator> _logger;

    public DisplayRuleEvaluator(ILogger<DisplayRuleEvaluator>? logger = null)
    {
        _logger = logger ?? NullLogger<DisplayRuleEvaluator>.Instance;
    }

    public CompiledDisplayRule Compile(DisplayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Cache.GetOrAdd(rule.Template, CompiledDisplayRule.Parse);
    }

    public string Evaluate(DisplayRule rule, IReadOnlyDictionary<string, object?> record) =>
        Evaluate(rule, record, null);

    public string Evaluate(
        DisplayRule rule,
        IReadOnlyDictionary<string, object?> record,
        string? collectionName)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(record);

        var compiled = Compile(rule);

        if (!compiled.IsValidSyntax)
        {
            _logger.LogWarning(
                "DisplayRule is invalid and cannot be evaluated. Collection: {CollectionName}, Error: {ParseError}",
                collectionName,
                compiled.ParseError);
            return string.Empty;
        }

        try
        {
            return Render(compiled, record, collectionName);
        }
        catch (Exception ex)
        {
            // Rendering must never break a record list.
            _logger.LogError(
                ex,
                "Unexpected DisplayRule evaluation failure. Collection: {CollectionName}",
                collectionName);
            return string.Empty;
        }
    }

    /// <summary>
    /// Renders the parsed segments.
    ///
    /// Null, missing and blank values are dropped together with one adjacent
    /// literal - the preceding one when there is one, otherwise the following
    /// one. That is what turns <c>"{FullName} - {Email}"</c> with a null Email
    /// into <c>"John Smith"</c> rather than <c>"John Smith - "</c>, while still
    /// keeping the prefix in <c>"Customer: {FullName}"</c>.
    /// </summary>
    private string Render(
        CompiledDisplayRule compiled,
        IReadOnlyDictionary<string, object?> record,
        string? collectionName)
    {
        var segments = compiled.Segments;
        var rendered = new string[segments.Count];
        var dropped = new bool[segments.Count];

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!segment.IsColumnReference)
            {
                rendered[i] = segment.Text;
                continue;
            }

            if (!TryGetValue(record, segment.Text, out var value))
            {
                _logger.LogWarning(
                    "DisplayRule references a missing column. Collection: {CollectionName}, Column: {ColumnName}",
                    collectionName,
                    segment.Text);
                rendered[i] = string.Empty;
            }
            else
            {
                rendered[i] = RecordValueFormatter.Format(value);
            }

            if (rendered[i].Length != 0)
            {
                continue;
            }

            // Drop the empty value together with one adjacent literal.
            dropped[i] = true;
            if (i > 0 && !segments[i - 1].IsColumnReference && !dropped[i - 1])
            {
                dropped[i - 1] = true;
            }
            else if (i + 1 < segments.Count && !segments[i + 1].IsColumnReference && !dropped[i + 1])
            {
                dropped[i + 1] = true;
            }
        }

        var builder = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            if (!dropped[i])
            {
                builder.Append(rendered[i]);
            }
        }

        var result = builder.ToString().Trim();

        _logger.LogDebug(
            "DisplayRule evaluated. Collection: {CollectionName}, References: {ColumnReferences}, ResultLength: {ResultLength}",
            collectionName,
            string.Join(",", compiled.ColumnReferences),
            result.Length);

        return result;
    }

    private static bool TryGetValue(
        IReadOnlyDictionary<string, object?> record,
        string columnName,
        out object? value)
    {
        if (record.TryGetValue(columnName, out value))
        {
            return true;
        }

        // Record dictionaries are not guaranteed to be case-insensitive.
        foreach (var pair in record)
        {
            if (string.Equals(pair.Key, columnName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

}
