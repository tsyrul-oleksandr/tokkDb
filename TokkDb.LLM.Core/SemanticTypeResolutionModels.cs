namespace TokkDb.LLM.Core;

public sealed record SemanticTypeResolutionInput(
    string ColumnName,
    string? ColumnDescription,
    IReadOnlyCollection<string> ExampleValues,
    IReadOnlyCollection<SemanticTypeToolResult> ExistingSemanticTypes,
    string? ExpectedBaseType = null);

public sealed record SemanticTypeResolutionResult(
    string? SuggestedSemanticTypeName,
    double Confidence,
    string Reason,
    SemanticTypeToolDefinition? ProposedSemanticType);

public sealed record SemanticTypeResolutionToolResult(
    string? SuggestedSemanticTypeName,
    double Confidence,
    string Reason,
    SemanticTypeToolDefinition? ProposedSemanticType);
