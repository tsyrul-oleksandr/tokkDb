namespace TokkDb.LLM.Core.Diagnostics;

public sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    DiagnosticLevel Level,
    string Source,
    string Category,
    string Title,
    string Summary,
    string? Details = null,
    string? Exception = null);
