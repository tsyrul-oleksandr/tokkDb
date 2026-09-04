using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace TokkDb.LLM.Core;

/// <summary>
/// Turns tool request and response objects into readable JSON for display.
///
/// Three rules govern this formatter:
/// <list type="bullet">
/// <item>It never throws. Serialization of an arbitrary tool payload can fail
/// (cycles, unsupported types); in that case a safe textual representation is
/// returned so the chat keeps working.</item>
/// <item>It redacts values whose property name looks like a credential, so
/// tokens, authorization headers, passwords and secrets are never displayed.</item>
/// <item>It caps the output so a very large result cannot flood the UI.</item>
/// </list>
/// </summary>
public static class ToolPayloadFormatter
{
    public const string RedactedPlaceholder = "***redacted***";

    private const int MaxLength = 20_000;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Property-name fragments that mark a value as sensitive. Matched
    /// case-insensitively against the whole property name.
    /// </summary>
    private static readonly string[] SensitiveNameFragments =
    [
        "token",
        "apikey",
        "api_key",
        "authorization",
        "auth",
        "password",
        "passwd",
        "secret",
        "credential",
        "bearer",
        "privatekey",
        "private_key",
        "connectionstring",
        "connection_string"
    ];

    /// <summary>
    /// Formats a value as indented JSON with sensitive values redacted.
    /// Returns <c>null</c> for a null payload so callers can omit the section.
    ///
    /// Serialization failures are never swallowed silently: they are logged with
    /// the affected type and tool name, and a safe textual representation is
    /// returned so the chat keeps working.
    /// </summary>
    /// <param name="value">Payload to format.</param>
    /// <param name="logger">Caller's logger, used to report serialization failures.</param>
    /// <param name="toolName">Tool the payload belongs to, when applicable.</param>
    /// <param name="operationId">Operation context, when available.</param>
    public static string? Format(
        object? value,
        ILogger? logger = null,
        string? toolName = null,
        string? operationId = null)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            // Round-trip through the DOM so redaction can be applied to the
            // serialized shape rather than to the CLR object graph.
            var element = JsonSerializer.SerializeToElement(value, WriteOptions);
            var redacted = Redact(element);
            var json = Cap(redacted.ToJsonString(WriteOptions));

            logger?.LogTrace(
                "Tool payload serialized. SerializedType: {SerializedType}, ToolName: {ToolName}, OperationId: {OperationId}, Length: {Length}",
                value.GetType().Name,
                toolName,
                operationId,
                json.Length);

            return json;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Recovery: fall back to text so the tool call still renders.
            logger?.LogError(
                ex,
                "Tool payload serialization failed; falling back to text. SerializedType: {SerializedType}, ToolName: {ToolName}, OperationId: {OperationId}",
                value.GetType().Name,
                toolName,
                operationId);

            return Cap(SafeText(value, logger));
        }
    }

    /// <summary>
    /// Formats an exception for display, without leaking a stack trace that may
    /// contain host paths.
    /// </summary>
    public static string FormatError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;

        return exception.InnerException is null
            ? Cap(message)
            : Cap($"{message}{Environment.NewLine}Caused by: {exception.InnerException.Message}");
    }

    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return SensitiveNameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static System.Text.Json.Nodes.JsonNode? Redact(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var result = new System.Text.Json.Nodes.JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    result[property.Name] = IsSensitiveName(property.Name)
                        ? System.Text.Json.Nodes.JsonValue.Create(RedactedPlaceholder)
                        : Redact(property.Value);
                }

                return result;
            }

            case JsonValueKind.Array:
            {
                var result = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    result.Add(Redact(item));
                }

                return result;
            }

            default:
                return System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());
        }
    }

    private static string SafeText(object value, ILogger? logger = null)
    {
        try
        {
            return value.ToString() ?? value.GetType().Name;
        }
        catch (Exception ex)
        {
            // A hostile or broken ToString() must not break the chat either.
            logger?.LogWarning(
                ex,
                "Fallback text representation failed. SerializedType: {SerializedType}",
                value.GetType().Name);
            return value.GetType().Name;
        }
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        return string.Concat(
            text.AsSpan(0, MaxLength),
            $"{Environment.NewLine}... truncated ({text.Length - MaxLength} more characters).");
    }
}
