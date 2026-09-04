namespace TokkDb.LLM.Core;

public sealed record LlmChunk(string Text, bool IsCompleted = false);
