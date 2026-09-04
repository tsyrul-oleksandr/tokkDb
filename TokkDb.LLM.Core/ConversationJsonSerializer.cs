using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokkDb.LLM.Core;

/// <summary>
/// Serializes a <see cref="ConversationExport"/> to JSON.
///
/// Two rules shape the output:
/// <list type="bullet">
/// <item>Only properties that actually exist are written, so an absent result or
/// error simply does not appear.</item>
/// <item>Tool payloads that are already JSON are embedded as JSON rather than as
/// an escaped string, which is what makes the export readable and machine
/// usable. A payload that is not JSON is written as a plain string.</item>
/// </list>
/// Message order is the order of the list: events are never grouped or sorted.
/// </summary>
public static class ConversationJsonSerializer
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static string Serialize(ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var messages = new JsonArray();
        foreach (var message in export.Messages)
        {
            messages.Add(BuildMessage(message));
        }

        var root = new JsonObject
        {
            ["version"] = export.Version,
            ["messages"] = messages
        };

        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject BuildMessage(ConversationExportMessage message)
    {
        var node = new JsonObject
        {
            ["id"] = message.Id,
            ["role"] = message.Role,
            ["timestamp"] = FormatTimestamp(message.Timestamp)
        };

        if (message.Tool is not null)
        {
            node["tool"] = BuildTool(message.Tool);
        }
        else if (message.Workflow is not null)
        {
            node["workflow"] = BuildWorkflow(message.Workflow);
        }
        else if (message.Records is not null)
        {
            node["records"] = BuildRecords(message.Records);
        }
        else
        {
            // A text-bearing message always carries content, null when it
            // produced none.
            node["content"] = message.Content is null ? null : JsonValue.Create(message.Content);
        }

        return node;
    }

    private static JsonObject BuildTool(ConversationExportTool tool)
    {
        var node = new JsonObject
        {
            ["name"] = tool.Name,
            ["status"] = tool.Status
        };

        AddPayload(node, "arguments", tool.Arguments);
        AddPayload(node, "result", tool.Result);

        if (!string.IsNullOrWhiteSpace(tool.Error))
        {
            // Errors are messages, not structured payloads.
            node["error"] = tool.Error;
        }

        return node;
    }

    private static JsonObject BuildWorkflow(ConversationExportWorkflow workflow)
    {
        var node = new JsonObject();

        if (!string.IsNullOrWhiteSpace(workflow.OperationId))
        {
            node["operationId"] = workflow.OperationId;
        }

        if (!string.IsNullOrWhiteSpace(workflow.Status))
        {
            node["status"] = workflow.Status;
        }

        if (!string.IsNullOrWhiteSpace(workflow.Message))
        {
            node["message"] = workflow.Message;
        }

        if (workflow.Actions.Count > 0)
        {
            var actions = new JsonArray();
            foreach (var action in workflow.Actions)
            {
                actions.Add(new JsonObject
                {
                    ["id"] = action.Id,
                    ["title"] = action.Title
                });
            }

            node["actions"] = actions;
        }

        return node;
    }

    private static JsonObject BuildRecords(ConversationExportRecords records)
    {
        var node = new JsonObject
        {
            ["collectionName"] = records.CollectionName,
            ["recordIds"] = ToArray(records.RecordIds)
        };

        if (records.AdditionalFields.Count > 0)
        {
            node["additionalFields"] = ToArray(records.AdditionalFields);
        }

        return node;
    }

    /// <summary>
    /// Writes a payload as embedded JSON when it parses, otherwise as a string.
    /// Absent payloads are omitted.
    /// </summary>
    private static void AddPayload(JsonObject node, string name, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        node[name] = TryParse(payload) ?? JsonValue.Create(payload);
    }

    private static JsonNode? TryParse(string payload)
    {
        var trimmed = payload.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            // Truncated or non-JSON payloads are exported verbatim as text.
            return null;
        }
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
