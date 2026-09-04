using System.Text.RegularExpressions;

namespace TokkDb.LLM.Storage;

public sealed partial class SemanticTypeRegistry : ISemanticTypeRegistry
{
    private readonly List<SemanticTypeDefinition> _definitions = new();

    public void Register(SemanticTypeDefinition definition)
    {
        var normalized = Normalize(definition);
        ValidateDefinition(normalized);

        _definitions.Add(normalized);
    }

    public bool Delete(string name)
    {
        var item = _definitions.FirstOrDefault(definition => definition.Name == name);
        if (item is null)
        {
            return false;
        }
        
        _definitions.Remove(item);
        if (!_definitions.Any(definition =>
                string.Equals(definition.ParentType, name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        
        _definitions.Add(item);
        return false;

    }

    public SemanticTypeDefinition? GetByNameOrAlias(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
        {
            return null;
        }
        
        var item = _definitions.FirstOrDefault(definition => definition.Name == nameOrAlias || 
            (definition.Aliases is not null && definition.Aliases.Contains(nameOrAlias)));

        return item;
    }

    public IReadOnlyCollection<SemanticTypeDefinition> GetAll()
    {
        return _definitions;
    }

    private SemanticTypeDefinition Normalize(SemanticTypeDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("Semantic type name is required.", nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            throw new ArgumentException("Semantic type display name is required.", nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.Description))
        {
            throw new ArgumentException("Semantic type description is required.", nameof(definition));
        }

        var aliases = (definition.Aliases ?? Array.Empty<string>())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var examples = (definition.Examples ?? Array.Empty<string>())
            .Where(example => !string.IsNullOrWhiteSpace(example))
            .Select(example => example.Trim())
            .ToArray();

        var normalizedValidationPatterns = StorageValidation.NormalizeValidationPatterns(
            definition.ValidationPattern,
            definition.ValidationPatterns);
        var normalizedNormalizationRules = StorageValidation.NormalizeNormalizationRules(definition.NormalizationRules);
        var normalizedValidations = NormalizeValidations(definition, normalizedValidationPatterns);

        return definition with
        {
            Name = definition.Name.Trim(),
            DisplayName = definition.DisplayName.Trim(),
            Description = definition.Description.Trim(),
            ParentType = string.IsNullOrWhiteSpace(definition.ParentType) ? null : definition.ParentType.Trim(),
            Aliases = aliases,
            Examples = examples,
            ValidationPattern = string.IsNullOrWhiteSpace(definition.ValidationPattern) ? null : definition.ValidationPattern.Trim(),
            ValidationPatterns = normalizedValidationPatterns,
            NormalizationRules = normalizedNormalizationRules,
            Validations = normalizedValidations
        };
    }

    /// <summary>
    /// Builds the canonical rule list: the rules given explicitly, plus the
    /// older pattern properties expressed as Regex rules. Each is checked
    /// against the base type here, so a rule that could never hold - a maximum
    /// on a string, a pattern on an integer - is refused at definition rather
    /// than rejecting every record later.
    /// </summary>
    private static IReadOnlyCollection<SemanticValidation> NormalizeValidations(
        SemanticTypeDefinition definition,
        IReadOnlyCollection<string> validationPatterns)
    {
        var normalized = new List<SemanticValidation>();

        foreach (var validation in definition.Validations ?? Array.Empty<SemanticValidation>())
        {
            if (validation is null)
            {
                continue;
            }

            normalized.Add(SemanticValidationRules.Normalize(validation, definition.BaseType, definition.Name));
        }

        foreach (var pattern in validationPatterns)
        {
            normalized.Add(SemanticValidationRules.Normalize(
                new SemanticValidation(SemanticValidationKind.Regex, Pattern: pattern),
                definition.BaseType,
                definition.Name));
        }

        return normalized.Distinct().ToArray();
    }

    private void ValidateDefinition(SemanticTypeDefinition definition)
    {
        if (!SemanticNameRegex().IsMatch(definition.Name))
        {
            throw new ArgumentException($"Semantic type name '{definition.Name}' is invalid.");
        }

        if (definition.Aliases is not null)
        {
            foreach (var alias in definition.Aliases)
            {
                if (!AliasRegex().IsMatch(alias))
                {
                    throw new ArgumentException($"Alias '{alias}' is invalid.");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.ParentType))
        {
            var parent = _definitions.FirstOrDefault(x => x.Name == definition.ParentType);
            if (parent is null)
            {
                throw new InvalidOperationException($"Parent semantic type '{definition.ParentType}' does not exist.");
            }

            if (parent.BaseType != definition.BaseType)
            {
                throw new InvalidOperationException(
                    $"Semantic type '{definition.Name}' base type '{definition.BaseType}' must match parent base type '{parent.BaseType}'.");
            }

            var cycleProbe = parent.ParentType;
            while (!string.IsNullOrWhiteSpace(cycleProbe))
            {
                if (string.Equals(cycleProbe, definition.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Semantic type hierarchy cannot contain cycles.");
                }

                cycleProbe = _definitions.FirstOrDefault(x => x.Name == cycleProbe)?.ParentType;
            }
        }

        if (definition.ValidationPatterns is not null)
        {
            foreach (var pattern in definition.ValidationPatterns)
            {
                StorageValidation.ValidateRegexPattern(pattern);
            }
        }

        if (definition.NormalizationRules is not null)
        {
            foreach (var rule in definition.NormalizationRules)
            {
                StorageValidation.ValidateNormalizationRule(rule);
            }
        }

        if ((definition.NormalizationRules?.Count ?? 0) > 0 &&
            definition.BaseType != Core.ColumnType.String)
        {
            throw new InvalidOperationException(
                $"Semantic type '{definition.Name}' normalization rules are only supported for String base type.");
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled)]
    private static partial Regex SemanticNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_\\-\\s]*$", RegexOptions.Compiled)]
    private static partial Regex AliasRegex();
}
