namespace TokkDb.LLM.Storage;

/// <summary>
/// A reusable refinement of a column: what the values mean, how they are
/// normalised before storage, and which rules they must satisfy.
///
/// <see cref="Validations"/> is the canonical rule list.
/// <see cref="ValidationPattern"/> and <see cref="ValidationPatterns"/> predate
/// it and remain as they were; the registry folds them into
/// <see cref="Validations"/> as Regex rules when a type is registered, so a
/// definition written either way behaves the same.
/// </summary>
public sealed record SemanticTypeDefinition(
    string Name,
    string DisplayName,
    string Description,
    Core.ColumnType BaseType,
    string? ParentType = null,
    IReadOnlyCollection<string>? Aliases = null,
    IReadOnlyCollection<string>? Examples = null,
    string? ValidationPattern = null,
    IReadOnlyCollection<string>? ValidationPatterns = null,
    IReadOnlyCollection<string>? NormalizationRules = null,
    IReadOnlyCollection<SemanticValidation>? Validations = null);
