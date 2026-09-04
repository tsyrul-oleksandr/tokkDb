using System.Text.RegularExpressions;

namespace TokkDb.LLM.Storage;

internal static partial class StorageValidation
{
    public static string NormalizeName(string name, string nameType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                $"{nameType} '{name}' is invalid. Use letters, digits, and underscores, and start with a letter.",
                nameof(name));
        }

        return name;
    }

    public static Core.ColumnType ParseColumnType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return type.Trim() switch
        {
            "String" => Core.ColumnType.String,
            "Boolean" or "Bool" => Core.ColumnType.Boolean,
            "Int32" or "Integer" => Core.ColumnType.Int32,
            "Int64" => Core.ColumnType.Int64,
            "Decimal" => Core.ColumnType.Decimal,
            "DateTime" => Core.ColumnType.DateTime,
            "Guid" => Core.ColumnType.Guid,
            var unknown => throw new ArgumentException($"Column type '{unknown}' is not supported.", nameof(type))
        };
    }

    public static bool IsValueCompatible(Core.ColumnType type, object? value)
    {
        if (value is null) return true;
        return type switch
        {
            Core.ColumnType.String => value is string,
            Core.ColumnType.Boolean => value is bool,
            Core.ColumnType.Int32 => value is int,
            Core.ColumnType.Int64 => value is long,
            Core.ColumnType.Decimal => value is decimal,
            Core.ColumnType.DateTime => value is DateTime,
            Core.ColumnType.Guid => value is Guid,
            _ => false
        };
    }

    public static IReadOnlyCollection<string> NormalizeValidationPatterns(
        string? validationPattern,
        IReadOnlyCollection<string>? validationPatterns)
    {
        var combined = new List<string>();
        if (!string.IsNullOrWhiteSpace(validationPattern))
        {
            combined.Add(validationPattern.Trim());
        }

        if (validationPatterns is not null)
        {
            combined.AddRange(validationPatterns
                .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(static pattern => pattern.Trim()));
        }

        var normalized = combined.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var pattern in normalized)
        {
            ValidateRegexPattern(pattern);
        }

        return normalized;
    }

    public static void ValidateRegexPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Validation pattern is required.", nameof(pattern));
        }

        _ = new Regex(pattern.Trim(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    public static bool MatchesAllValidationPatterns(object? value, IReadOnlyCollection<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }

        if (value is not string stringValue)
        {
            return false;
        }

        return patterns.All(pattern =>
            Regex.IsMatch(
                stringValue,
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250)));
    }

    public static IReadOnlyCollection<string> NormalizeNormalizationRules(IReadOnlyCollection<string>? normalizationRules)
    {
        if (normalizationRules is null || normalizationRules.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = normalizationRules
            .Where(static rule => !string.IsNullOrWhiteSpace(rule))
            .Select(static rule => rule.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var rule in normalized)
        {
            ValidateNormalizationRule(rule);
        }

        return normalized;
    }

    public static object ApplyNormalizationRules(object value, IReadOnlyCollection<string>? normalizationRules)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (normalizationRules is null || normalizationRules.Count == 0)
        {
            return value;
        }

        if (value is not string normalizedValue)
        {
            throw new InvalidOperationException("Normalization rules can only be applied to string values.");
        }

        foreach (var rule in normalizationRules)
        {
            if (string.Equals(rule, "Trim", StringComparison.OrdinalIgnoreCase))
            {
                normalizedValue = normalizedValue.Trim();
                continue;
            }

            if (string.Equals(rule, "ToLowerInvariant", StringComparison.OrdinalIgnoreCase))
            {
                normalizedValue = normalizedValue.ToLowerInvariant();
                continue;
            }

            if (string.Equals(rule, "ToUpperInvariant", StringComparison.OrdinalIgnoreCase))
            {
                normalizedValue = normalizedValue.ToUpperInvariant();
                continue;
            }

            if (string.Equals(rule, "RemoveWhitespace", StringComparison.OrdinalIgnoreCase))
            {
                normalizedValue = new string(normalizedValue.Where(static ch => !char.IsWhiteSpace(ch)).ToArray());
                continue;
            }

            if (rule.StartsWith("RemoveCharacters:", StringComparison.OrdinalIgnoreCase))
            {
                var characters = rule["RemoveCharacters:".Length..];
                if (characters.Length == 0)
                {
                    throw new InvalidOperationException("Normalization rule 'RemoveCharacters' requires at least one character.");
                }

                normalizedValue = new string(normalizedValue.Where(ch => !characters.Contains(ch, StringComparison.Ordinal)).ToArray());
                continue;
            }

            throw new InvalidOperationException($"Normalization rule '{rule}' is not supported.");
        }

        return normalizedValue;
    }

    public static void ValidateNormalizationRule(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            throw new ArgumentException("Normalization rule is required.", nameof(rule));
        }

        var trimmed = rule.Trim();
        if (string.Equals(trimmed, "Trim", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "ToLowerInvariant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "ToUpperInvariant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "RemoveWhitespace", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (trimmed.StartsWith("RemoveCharacters:", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > "RemoveCharacters:".Length)
        {
            return;
        }

        throw new ArgumentException($"Normalization rule '{rule}' is not supported.", nameof(rule));
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled)]
    private static partial Regex NameRegex();
}
