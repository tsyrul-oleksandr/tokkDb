namespace TokkDb.LLM.Core;

public interface ILLMProvider
{
    IAsyncEnumerable<LlmChunk> StreamResponseAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
