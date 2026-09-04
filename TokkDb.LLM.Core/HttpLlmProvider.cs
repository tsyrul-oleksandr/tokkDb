using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TokkDb.LLM.Core;

public sealed class HttpLlmProvider : ILLMProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public HttpLlmProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<LlmChunk> StreamResponseAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        using var httpRequest = BuildHttpRequest(request);
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new LlmProviderException(
                $"LLM provider returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                (int)response.StatusCode,
                body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (request.Provider == LlmProviderKind.OpenAiCompatible)
            {
                foreach (var chunk in ParseOpenAiLine(line))
                {
                    yield return chunk;
                }

                continue;
            }

            foreach (var chunk in ParseOllamaLine(line))
            {
                yield return chunk;
            }
        }

        yield return new LlmChunk(string.Empty, true);
    }

    private static void ValidateRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
    }

    private static HttpRequestMessage BuildHttpRequest(LlmRequest request)
    {
        var endpoint = BuildEndpoint(request);
        var payload = request.Provider switch
        {
            LlmProviderKind.OpenAiCompatible => BuildOpenAiPayload(request),
            LlmProviderKind.Ollama => BuildOllamaPayload(request),
            _ => throw new LlmProviderException($"Unsupported provider type '{request.Provider}'.")
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(request.AuthenticationToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AuthenticationToken);
        }

        return httpRequest;
    }

    private static string BuildEndpoint(LlmRequest request)
    {
        var baseUri = request.Url.TrimEnd('/');
        return request.Provider switch
        {
            LlmProviderKind.OpenAiCompatible => baseUri.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUri
                : $"{baseUri}/v1/chat/completions",
            LlmProviderKind.Ollama => baseUri.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)
                ? baseUri
                : $"{baseUri}/api/chat",
            _ => baseUri
        };
    }

    private static string BuildOpenAiPayload(LlmRequest request)
    {
        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "user", ["content"] = request.Prompt }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Insert(0, new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = messages
        };

        return JsonSerializer.Serialize(body, JsonOptions);
    }

    private static string BuildOllamaPayload(LlmRequest request)
    {
        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "user", ["content"] = request.Prompt }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Insert(0, new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = messages
        };

        return JsonSerializer.Serialize(body, JsonOptions);
    }

    private static IReadOnlyCollection<LlmChunk> ParseOpenAiLine(string line)
    {
        var chunks = new List<LlmChunk>();

        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return chunks;
        }

        var payload = line[5..].Trim();
        if (payload == "[DONE]")
        {
            chunks.Add(new LlmChunk(string.Empty, true));
            return chunks;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return chunks;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    chunks.Add(new LlmChunk(text));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException("Failed to parse streaming response from OpenAI-compatible provider.", innerException: ex);
        }

        return chunks;
    }

    private static IReadOnlyCollection<LlmChunk> ParseOllamaLine(string line)
    {
        var chunks = new List<LlmChunk>();

        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;

            if (root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    chunks.Add(new LlmChunk(text));
                }
            }

            if (root.TryGetProperty("done", out var done) &&
                done.ValueKind == JsonValueKind.True)
            {
                chunks.Add(new LlmChunk(string.Empty, true));
            }
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException("Failed to parse streaming response from Ollama.", innerException: ex);
        }

        return chunks;
    }
}
