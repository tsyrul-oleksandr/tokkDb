using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TokkDb.LLM.Storage;

public sealed record DisplayRuleValidationError(string Code, string Message, string? ColumnName = null);

public sealed record DisplayRuleValidationResult(
    bool IsValid,
    IReadOnlyCollection<DisplayRuleValidationError> Errors,
    IReadOnlyCollection<string> ReferencedColumns)
{
    public static DisplayRuleValidationResult Valid(IReadOnlyCollection<string> referencedColumns) =>
        new(true, Array.Empty<DisplayRuleValidationError>(), referencedColumns);

    public static DisplayRuleValidationResult Invalid(
        IReadOnlyCollection<DisplayRuleValidationError> errors,
        IReadOnlyCollection<string>? referencedColumns = null) =>
        new(false, errors, referencedColumns ?? Array.Empty<string>());

    /// <summary>Column names referenced by the rule that the collection does not define.</summary>
    public IReadOnlyCollection<string> MissingColumns =>
        Errors.Where(error => error.Code == DisplayRuleValidator.MissingColumnCode)
            .Select(error => error.ColumnName ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray();
}

public interface IDisplayRuleValidator
{
    DisplayRuleValidationResult Validate(DisplayRule rule, CollectionDefinition collection);

    /// <summary>
    /// Validates against a bare column list, for checking a rule against a
    /// schema that is about to change.
    /// </summary>
    DisplayRuleValidationResult Validate(
        DisplayRule rule,
        string collectionName,
        IReadOnlyCollection<string> columnNames);
}

/// <summary>
/// Deterministic DisplayRule validation. Never calls an LLM.
/// </summary>
public sealed class DisplayRuleValidator : IDisplayRuleValidator
{
    public const string SyntaxErrorCode = "DisplayRuleSyntax";
    public const string MissingColumnCode = "DisplayRuleMissingColumn";

    private readonly IDisplayRuleEvaluator _evaluator;
    private readonly ILogger<DisplayRuleValidator> _logger;

    public DisplayRuleValidator(
        IDisplayRuleEvaluator evaluator,
        ILogger<DisplayRuleValidator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _logger = logger ?? NullLogger<DisplayRuleValidator>.Instance;
    }

    public DisplayRuleValidationResult Validate(DisplayRule rule, CollectionDefinition collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return Validate(rule, collection.Name, collection.Columns.Select(column => column.Name).ToArray());
    }

    public DisplayRuleValidationResult Validate(
        DisplayRule rule,
        string collectionName,
        IReadOnlyCollection<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(columnNames);

        var compiled = _evaluator.Compile(rule);

        if (!compiled.IsValidSyntax)
        {
            _logger.LogWarning(
                "DisplayRule syntax is invalid. Collection: {CollectionName}, Error: {ParseError}",
                collectionName,
                compiled.ParseError);

            return DisplayRuleValidationResult.Invalid(
            [
                new DisplayRuleValidationError(
                    SyntaxErrorCode,
                    compiled.ParseError ?? "Template could not be parsed.")
            ]);
        }

        var known = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase);
        var errors = new List<DisplayRuleValidationError>();

        foreach (var reference in compiled.ColumnReferences)
        {
            if (known.Contains(reference))
            {
                continue;
            }

            _logger.LogWarning(
                "DisplayRule references a missing column. Collection: {CollectionName}, Column: {ColumnName}",
                collectionName,
                reference);

            errors.Add(new DisplayRuleValidationError(
                MissingColumnCode,
                $"Column '{reference}' does not exist in collection '{collectionName}'.",
                reference));
        }

        return errors.Count == 0
            ? DisplayRuleValidationResult.Valid(compiled.ColumnReferences)
            : DisplayRuleValidationResult.Invalid(errors, compiled.ColumnReferences);
    }
}
