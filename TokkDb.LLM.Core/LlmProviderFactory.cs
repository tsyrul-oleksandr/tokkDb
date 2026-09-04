using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace TokkDb.LLM.Core;

internal static class LlmProviderFactory
{
    internal static IChatClient CreateChatClient(LlmProviderKind kind, string url, string model, 
        string? authenticationToken)
    {
        return kind switch
        {
            LlmProviderKind.OpenAiCompatible => new ChatClient(model, 
                new ApiKeyCredential(authenticationToken ?? string.Empty),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(url)
                }).AsIChatClient(),
            LlmProviderKind.Ollama => new OllamaApiClient(url, model),
            _ => throw new InvalidOperationException($"Unsupported provider: {kind}")
        };
    }

    /// <summary>
    /// Applies provider-specific request options.
    ///
    /// Ollama sizes the context window per request and defaults to 4096, which a
    /// large tool surface exhausts quickly, so the configured size is sent with
    /// every call. OpenAI-compatible endpoints size their context server side
    /// and take no equivalent option.
    /// </summary>
    internal static void ApplyProviderOptions(ChatOptions options, LlmProviderKind kind, int? contextSize)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (contextSize is null or <= 0)
        {
            return;
        }

        if (kind == LlmProviderKind.Ollama)
        {
            options.AddOllamaOption(OllamaOption.NumCtx, contextSize.Value);
        }
    }
}
