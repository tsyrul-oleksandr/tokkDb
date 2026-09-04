using System.Globalization;
using System.Text.RegularExpressions;

namespace TokkDb.LLM.Storage;

/// <summary>
/// The kinds of refinement a semantic type can place on a column's values.
///
/// Each kind applies only to certain base types - a regular expression means
/// nothing on an integer, a maximum means nothing on a boolean - and that
/// restriction is enforced when the type is defined rather than when a record
/// is written. Otherwise a mismatch is invisible until every insert fails.
/// </summary>
public enum SemanticValidationKind
{
    Regex,
    MinLength,
    MaxLength,
    MinValue,
    MaxValue
}

/// <summary>
/// One rule belonging to a semantic type.
///
/// The parameters are deliberately flat rather than a type per kind: this shape
/// serialises without a converter and can be handed to a model as-is. Only the
/// parameter its kind uses is read; <see cref="SemanticValidationRules.Normalize"/>
/// rejects a rule that is missing it.
/// </summary>
public sealed record SemanticValidation(
    SemanticValidationKind Kind,
    string? Pattern = null,
    int? Length = null,
    string? Value = null);

/// <summary>
/// Definition-time checking and record-time evaluation of semantic validations.
///
/// Both live here so that what a rule <em>requires</em> and what makes a value
/// <em>fail</em> it are written next to each other: the message a caller reads
/// after a rejection is generated from the same description used when the rule
/// is explained.
/// </summary>
public static class SemanticValidationRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Core.ColumnType[] TextTypes = [Core.ColumnType.String];

    private static readonly Core.ColumnType[] OrderedTypes =
    [
        Core.ColumnType.Int32,
        Core.ColumnType.Int64,
        Core.ColumnType.Decimal,
        Core.ColumnType.DateTime
    ];

    /// <summary>
    /// Base types a kind can be applied to.
    /// </summary>
    public static IReadOnlyCollection<Core.ColumnType> CompatibleTypes(SemanticValidationKind kind) =>
        kind switch
        {
            SemanticValidationKind.Regex or
                SemanticValidationKind.MinLength or
                SemanticValidationKind.MaxLength => TextTypes,

            SemanticValidationKind.MinValue or
                SemanticValidationKind.MaxValue => OrderedTypes,

            _ => Array.Empty<Core.ColumnType>()
        };

    /// <summary>
    /// Checks a rule against the base type it will be applied to and returns it
    /// in canonical form.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The rule does not suit the base type, or its parameter is missing or
    /// unusable.
    /// </exception>
    public static SemanticValidation Normalize(
        SemanticValidation validation,
        Core.ColumnType baseType,
        string semanticTypeName)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var compatible = CompatibleTypes(validation.Kind);
        if (compatible.Count == 0)
        {
            throw new ArgumentException(
                $"Validation kind '{validation.Kind}' on semantic type '{semanticTypeName}' is not supported.");
        }

        if (!compatible.Contains(baseType))
        {
            throw new ArgumentException(
                $"Validation '{validation.Kind}' on semantic type '{semanticTypeName}' cannot be used with base type " +
                $"{baseType}. It applies to {string.Join(", ", compatible)}.");
        }

        switch (validation.Kind)
        {
            case SemanticValidationKind.Regex:
            {
                if (string.IsNullOrWhiteSpace(validation.Pattern))
                {
                    throw new ArgumentException(
                        $"Validation 'Regex' on semantic type '{semanticTypeName}' requires a pattern.");
                }

                var pattern = validation.Pattern.Trim();
                StorageValidation.ValidateRegexPattern(pattern);
                return new SemanticValidation(validation.Kind, Pattern: pattern);
            }

            case SemanticValidationKind.MinLength:
            case SemanticValidationKind.MaxLength:
            {
                if (validation.Length is null)
                {
                    throw new ArgumentException(
                        $"Validation '{validation.Kind}' on semantic type '{semanticTypeName}' requires a length.");
                }

                if (validation.Length < 0)
                {
                    throw new ArgumentException(
                        $"Validation '{validation.Kind}' on semantic type '{semanticTypeName}' requires a length of zero or more.");
                }

                return new SemanticValidation(validation.Kind, Length: validation.Length);
            }

            default:
            {
                if (string.IsNullOrWhiteSpace(validation.Value))
                {
                    throw new ArgumentException(
                        $"Validation '{validation.Kind}' on semantic type '{semanticTypeName}' requires a value.");
                }

                var text = validation.Value.Trim();
                if (!TryParseBound(text, baseType, out _))
                {
                    throw new ArgumentException(
                        $"Validation '{validation.Kind}' on semantic type '{semanticTypeName}' has value '{text}', " +
                        $"which is not a valid {baseType}.");
                }

                return new SemanticValidation(validation.Kind, Value: text);
            }
        }
    }

    /// <summary>
    /// What the rule demands, phrased so it can be shown to whoever supplied the
    /// offending value.
    /// </summary>
    public static string Describe(SemanticValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        return validation.Kind switch
        {
            SemanticValidationKind.Regex => $"must match the pattern {validation.Pattern}",
            SemanticValidationKind.MinLength => $"must be at least {validation.Length} character(s) long",
            SemanticValidationKind.MaxLength => $"must be at most {validation.Length} character(s) long",
            SemanticValidationKind.MinValue => $"must be {validation.Value} or greater",
            SemanticValidationKind.MaxValue => $"must be {validation.Value} or less",
            _ => "must satisfy an unknown rule"
        };
    }

    /// <summary>
    /// Whether a stored value satisfies the rule. A value of the wrong runtime
    /// type does not satisfy it, but that is reported separately as a type
    /// error, so callers should check type compatibility first.
    /// </summary>
    public static bool IsSatisfied(SemanticValidation validation, object value)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(value);

        switch (validation.Kind)
        {
            case SemanticValidationKind.Regex:
                return value is string text &&
                       !string.IsNullOrEmpty(validation.Pattern) &&
                       Regex.IsMatch(text, validation.Pattern, RegexOptions.CultureInvariant, RegexTimeout);

            case SemanticValidationKind.MinLength:
                return value is string minText && minText.Length >= (validation.Length ?? 0);

            case SemanticValidationKind.MaxLength:
                return value is string maxText && maxText.Length <= (validation.Length ?? int.MaxValue);

            case SemanticValidationKind.MinValue:
            case SemanticValidationKind.MaxValue:
            {
                if (value is not IComparable comparable ||
                    !TryParseBound(validation.Value, ColumnTypeOf(value), out var bound) ||
                    bound is null)
                {
                    return false;
                }

                var comparison = comparable.CompareTo(bound);
                return validation.Kind == SemanticValidationKind.MinValue ? comparison >= 0 : comparison <= 0;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Evaluates every rule and returns the ones the value fails, in order.
    /// </summary>
    public static IReadOnlyList<SemanticValidation> Failing(
        IReadOnlyCollection<SemanticValidation>? validations,
        object value)
    {
        if (validations is null || validations.Count == 0)
        {
            return Array.Empty<SemanticValidation>();
        }

        return validations.Where(validation => !IsSatisfied(validation, value)).ToArray();
    }

    /// <summary>
    /// Parses a bound to the type it will be compared against. The bound is
    /// stored as text so a definition stays serialisable; comparison happens
    /// against the real value type.
    /// </summary>
    private static bool TryParseBound(string? text, Core.ColumnType baseType, out object? bound)
    {
        bound = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        switch (baseType)
        {
            case Core.ColumnType.Int32:
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    bound = intValue;
                }

                break;

            case Core.ColumnType.Int64:
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    bound = longValue;
                }

                break;

            case Core.ColumnType.Decimal:
                if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
                {
                    bound = decimalValue;
                }

                break;

            case Core.ColumnType.DateTime:
                if (DateTime.TryParse(
                        trimmed,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var dateValue))
                {
                    bound = dateValue;
                }

                break;
        }

        return bound is not null;
    }

    private static Core.ColumnType ColumnTypeOf(object value) =>
        value switch
        {
            int => Core.ColumnType.Int32,
            long => Core.ColumnType.Int64,
            decimal => Core.ColumnType.Decimal,
            DateTime => Core.ColumnType.DateTime,
            bool => Core.ColumnType.Boolean,
            Guid => Core.ColumnType.Guid,
            _ => Core.ColumnType.String
        };
}
