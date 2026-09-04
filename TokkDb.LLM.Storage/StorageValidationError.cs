namespace TokkDb.LLM.Storage;

public sealed record StorageValidationError(string Code, string? ColumnName, string Message);
