using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace TokkDb.LLM.Core;

public sealed class SemanticTypeAgent : ISemanticTypeAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SemanticTypeAgent> _logger;

    public SemanticTypeAgent(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<SemanticTypeAgent> logger)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<SemanticTypeResolutionResult> ResolveAsync(
        ConversationRequest providerConfiguration,
        SemanticTypeResolutionInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);

        var semanticRequest = providerConfiguration with
        {
            Message = "semantic-type-resolution",
            SystemPrompt = BuildSystemPrompt()
        };

        using var chatClient = LlmProviderFactory.CreateChatClient(providerConfiguration.Provider,
            providerConfiguration.Url, providerConfiguration.Model, providerConfiguration.AuthenticationToken);

        var response = await chatClient.GetResponseAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                new ChatMessage(ChatRole.User, BuildUserPrompt(input))
            },
            new ChatOptions
            {
                ModelId = providerConfiguration.Model
            },
            cancellationToken);

        var text = response.Text ?? string.Empty;
        return ParseResolution(text);
    }

    private static string BuildSystemPrompt()
    {
        return "You are a semantic type classifier for a dynamic record store. " +
               "You are given one column - its name, description, expected base type and example values - " +
               "together with the semantic types already registered. " +
               "Choose the existing semantic type that fits best; propose a new one only when none of them do. " +
               "Reply with a single JSON object and nothing else: no prose, no explanation, no markdown code fences. " +
               "Shape: " +
               "{\"suggestedSemanticTypeName\":string|null,\"confidence\":number,\"reason\":string,\"proposedSemanticType\":object|null}. " +
               "Set suggestedSemanticTypeName to the name of an existing type, or null when proposing a new one. " +
               "Set proposedSemanticType only when proposing, and leave it null otherwise. " +
               "confidence is a number between 0 and 1. " +
               "reason is one short sentence explaining the choice.";
    }

    private static string BuildUserPrompt(SemanticTypeResolutionInput input)
    {
        var payload = new
        {
            columnName = input.ColumnName,
            columnDescription = input.ColumnDescription,
            expectedBaseType = input.ExpectedBaseType,
            exampleValues = input.ExampleValues,
            semanticTypes = input.ExistingSemanticTypes.Select(type => new
            {
                type.Name,
                type.DisplayName,
                type.Description,
                type.BaseType,
                type.ParentType,
                type.Aliases,
                type.Examples,
                type.ValidationPattern,
                type.ValidationPatterns,
                type.NormalizationRules
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var builder = new StringBuilder();
        builder.AppendLine("Analyze the column and map it to the best semantic type.");
        builder.AppendLine("If no suitable semantic type exists, provide proposedSemanticType with full definition.");
        builder.AppendLine("Column payload:");
        builder.AppendLine(json);
        return builder.ToString();
    }

    private SemanticTypeResolutionResult ParseResolution(string rawText)
    {
        try
        {
            using var json = JsonDocument.Parse(ExtractJsonObject(rawText));
            var root = json.RootElement;

            string? suggested = null;
            if (root.TryGetProperty("suggestedSemanticTypeName", out var suggestedElement) &&
                suggestedElement.ValueKind == JsonValueKind.String)
            {
                suggested = suggestedElement.GetString();
            }

            var confidence = 0d;
            if (root.TryGetProperty("confidence", out var confidenceElement) &&
                confidenceElement.ValueKind is JsonValueKind.Number &&
                confidenceElement.TryGetDouble(out var parsed))
            {
                confidence = Math.Clamp(parsed, 0d, 1d);
            }

            var reason = root.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() ?? "No reason provided."
                : "No reason provided.";

            SemanticTypeToolDefinition? proposal = null;
            if (root.TryGetProperty("proposedSemanticType", out var proposedElement) &&
                proposedElement.ValueKind == JsonValueKind.Object)
            {
                proposal = ParseProposedType(proposedElement);
            }

            return new SemanticTypeResolutionResult(suggested, confidence, reason, proposal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic type resolution response parse failed.");
            throw new InvalidOperationException("Semantic type agent returned an invalid response.", ex);
        }
    }

    private static SemanticTypeToolDefinition ParseProposedType(JsonElement element)
    {
        static List<string> ReadStringArray(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return arr.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
        }

        return new SemanticTypeToolDefinition(
            Name: element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty,
            DisplayName: element.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String ? displayName.GetString() ?? string.Empty : string.Empty,
            Description: element.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() ?? string.Empty : string.Empty,
            BaseType: element.TryGetProperty("baseType", out var baseType) && baseType.ValueKind == JsonValueKind.String && Enum.TryParse<ColumnType>(baseType.GetString(), out var parsedBaseType) ? parsedBaseType : ColumnType.String,
            ParentType: element.TryGetProperty("parentType", out var parentType) && parentType.ValueKind == JsonValueKind.String ? parentType.GetString() : null,
            Aliases: ReadStringArray(element, "aliases"),
            Examples: ReadStringArray(element, "examples"),
            ValidationPattern: element.TryGetProperty("validationPattern", out var pattern) && pattern.ValueKind == JsonValueKind.String ? pattern.GetString() : null,
            ValidationPatterns: ReadStringArray(element, "validationPatterns"),
            NormalizationRules: ReadStringArray(element, "normalizationRules"));
    }

    private static string ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        throw new InvalidOperationException("No JSON object found in semantic type response.");
    }

    private static void ValidateInput(SemanticTypeResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ColumnName);
        ArgumentNullException.ThrowIfNull(input.ExampleValues);
        ArgumentNullException.ThrowIfNull(input.ExistingSemanticTypes);
    }
}
