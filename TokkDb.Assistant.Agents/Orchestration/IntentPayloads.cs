using System.Text.Json.Nodes;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// What a waiting request holds, written and read (D-15, AG-8): the validated, resolved intent
/// as a recipe that re-plans to the same proposal deterministically - the incoming shape, what
/// the model answered, the policy - with the content hash the card was shown under (AG-3d). On
/// resume the recipe is re-planned with no model call and the hash compared: the same, and it
/// runs; the schema moved since, and it re-plans (AG-9); anything else, and it was substituted.
/// </summary>
public static class IntentPayloads
{
    public const string PlacementKind = "placement";
    public const string StructureKind = "structure";
    public const string DeleteKind = "delete";
    public const string PartialUndoKind = "partial-undo";

    public static string QuestionPayload(Ulid requestId) => new JsonObject { ["kind"] = "question", ["request"] = requestId.ToString() }.ToJsonString();

    // ---- Placement ------------------------------------------------------------------------------------

    public static RequestIntent Placement(IncomingShape shape, MappingAnswer? answer, MergePolicy policy, string schemaVersion, string message, string hash)
    {
        var json = new JsonObject
        {
            ["shape"] = WriteShape(shape),
            ["answer"] = answer is null ? null : new JsonObject
            {
                ["choice"] = answer.Choice,
                ["newName"] = answer.NewName,
                ["purpose"] = answer.Purpose,
                ["fields"] = new JsonArray([.. answer.Fields.Select(field => (JsonNode)new JsonArray(field.Incoming, field.Existing))])
            },
            ["policy"] = policy.ToString(),
            ["schema"] = schemaVersion,
            ["message"] = message
        };

        return new RequestIntent(PlacementKind, json.ToJsonString(), hash);
    }

    public static (IncomingShape Shape, MappingAnswer? Answer, MergePolicy Policy, string SchemaVersion, string Message) ReadPlacement(string payload)
    {
        var json = JsonNode.Parse(payload)!.AsObject();
        var shape = ReadShape(json["shape"]!.AsObject());
        var answer = json["answer"] is JsonObject a
            ? new MappingAnswer(
                a["choice"]?.ToString() ?? "none",
                a["newName"]?.ToString(),
                a["purpose"]?.ToString(),
                [.. (a["fields"]?.AsArray() ?? []).Select(static pair => (pair![0]!.ToString(), pair[1]?.ToString() ?? ""))])
            : null;

        return (shape, answer,
            Enum.TryParse<MergePolicy>(json["policy"]?.ToString(), out var policy) ? policy : MergePolicy.SkipExisting,
            json["schema"]?.ToString() ?? "",
            json["message"]?.ToString() ?? "");
    }

    private static JsonObject WriteShape(IncomingShape shape) => new()
    {
        ["name"] = shape.Name,
        ["purpose"] = shape.Purpose,
        ["source"] = shape.Source,
        ["fields"] = new JsonArray([.. shape.Fields.Select(field => (JsonNode)new JsonObject
        {
            ["name"] = field.Name,
            ["kind"] = field.Kind.ToString(),
            ["majority"] = field.MajorityKind.ToString(),
            ["examples"] = new JsonArray([.. field.Examples.Select(static example => (JsonNode)example)]),
            ["values"] = field.ValueCount,
            ["blanks"] = field.BlankCount,
            ["exceptions"] = new JsonArray([.. field.ExceptionLines.Select(static line => (JsonNode)line)])
        })]),
        ["rows"] = new JsonArray([.. shape.Rows.Select(row => (JsonNode)new JsonObject
        {
            ["line"] = row.LineNumber,
            ["unreadable"] = row.Unreadable,
            ["fields"] = Values.EncodeFields(row.Fields)
        })])
    };

    private static IncomingShape ReadShape(JsonObject json) => new(
        json["name"]!.ToString(),
        json["purpose"]?.ToString(),
        [.. (json["fields"]?.AsArray() ?? []).Select(static field => new IncomingField(
            field!["name"]!.ToString(),
            Enum.Parse<ColumnType>(field["kind"]!.ToString()),
            Enum.Parse<ColumnType>(field["majority"]!.ToString()),
            [.. (field["examples"]?.AsArray() ?? []).Select(static example => example!.ToString())],
            field["values"]?.GetValue<int>() ?? 0,
            field["blanks"]?.GetValue<int>() ?? 0,
            [.. (field["exceptions"]?.AsArray() ?? []).Select(static line => line!.GetValue<int>())]))],
        [.. (json["rows"]?.AsArray() ?? []).Select(static row => new IncomingRow(
            Values.DecodeFields(row!["fields"]?.AsObject()),
            row["line"]?.GetValue<int?>(),
            row["unreadable"]?.ToString()))],
        json["source"]?.ToString() ?? "");

    // ---- Structural change ----------------------------------------------------------------------------

    public static RequestIntent Structure(StructuralAction action, string schemaVersion)
    {
        var json = WriteAction(action);
        json["schema"] = schemaVersion;
        var payload = json.ToJsonString();
        return new RequestIntent(StructureKind, payload, Hashes.Of(payload));
    }

    public static (StructuralAction Action, string SchemaVersion) ReadStructure(string payload)
    {
        var json = JsonNode.Parse(payload)!.AsObject();
        return (ReadAction(json), json["schema"]?.ToString() ?? "");
    }

    public static JsonObject WriteAction(StructuralAction action) => action switch
    {
        RemoveField remove => new JsonObject { ["action"] = "remove-field", ["thing"] = remove.Thing, ["field"] = remove.Field },
        RenameField rename => new JsonObject { ["action"] = "rename-field", ["thing"] = rename.Thing, ["field"] = rename.Field, ["newName"] = rename.NewName },
        RetypeField retype => new JsonObject { ["action"] = "retype-field", ["thing"] = retype.Thing, ["field"] = retype.Field, ["kind"] = retype.NewType.ToString() },
        AddField add => new JsonObject { ["action"] = "add-field", ["thing"] = add.Thing, ["field"] = add.Column.Name, ["kind"] = add.Column.Type.ToString(), ["required"] = add.Column.Required, ["unique"] = add.Column.Unique },
        MakeUnique unique => new JsonObject { ["action"] = "set-unique", ["thing"] = unique.Thing, ["field"] = unique.Field, ["on"] = unique.Unique },
        MakeRequired required => new JsonObject { ["action"] = "set-required", ["thing"] = required.Thing, ["field"] = required.Field, ["on"] = required.Required },
        RemoveThing remove => new JsonObject { ["action"] = "remove-thing", ["thing"] = remove.Thing },
        DeleteRecords delete => new JsonObject { ["action"] = "delete", ["thing"] = delete.Thing, ["ids"] = new JsonArray([.. delete.Ids.Select(static id => (JsonNode)id.ToString())]) },
        SetDisplayRule rule => new JsonObject { ["action"] = "display-rule", ["thing"] = rule.Thing, ["rule"] = rule.Rule?.Template },
        EraseRecords erase => new JsonObject { ["action"] = "erase", ["thing"] = erase.Thing, ["ids"] = new JsonArray([.. erase.Ids.Select(static id => (JsonNode)id.ToString())]) },
        ChangeRecord change => new JsonObject { ["action"] = "change-record", ["thing"] = change.Thing, ["id"] = change.Id.ToString(), ["field"] = change.Field, ["value"] = Values.Encode(change.Value) },
        _ => throw new NotSupportedException($"A {action.GetType().Name} cannot be held.")
    };

    public static StructuralAction ReadAction(JsonObject json)
    {
        var thing = json["thing"]!.ToString();
        var field = json["field"]?.ToString() ?? "";

        return json["action"]?.ToString() switch
        {
            "remove-field" => new RemoveField(thing, field),
            "rename-field" => new RenameField(thing, field, json["newName"]!.ToString()),
            "retype-field" => new RetypeField(thing, field, Enum.Parse<ColumnType>(json["kind"]!.ToString())),
            "add-field" => new AddField(thing, new ColumnDefinition(field, Enum.Parse<ColumnType>(json["kind"]!.ToString()),
                required: json["required"]?.GetValue<bool>() ?? false, unique: json["unique"]?.GetValue<bool>() ?? false)),
            "set-unique" => new MakeUnique(thing, field, json["on"]?.GetValue<bool>() ?? true),
            "set-required" => new MakeRequired(thing, field, json["on"]?.GetValue<bool>() ?? true),
            "remove-thing" => new RemoveThing(thing),
            "delete" => new DeleteRecords(thing, [.. (json["ids"]?.AsArray() ?? []).Select(static id => Ulid.Parse(id!.ToString()))]),
            "display-rule" => new SetDisplayRule(thing, json["rule"] is { } template ? new DisplayRule(template.ToString()) : null),
            "erase" => new EraseRecords(thing, [.. (json["ids"]?.AsArray() ?? []).Select(static id => Ulid.Parse(id!.ToString()))]),
            "change-record" => new ChangeRecord(thing, Ulid.Parse(json["id"]!.ToString()), field, Values.Decode(json["value"]?.ToString())),
            var other => throw new NotSupportedException($"'{other}' is not an action that can be held.")
        };
    }

    // ---- Partial undo ---------------------------------------------------------------------------------

    public static RequestIntent PartialUndo(Ulid undoneRequest, IReadOnlyList<Ulid> includedChanges, IReadOnlyList<string> leftOut)
    {
        var json = new JsonObject
        {
            ["request"] = undoneRequest.ToString(),
            ["include"] = new JsonArray([.. includedChanges.Select(static id => (JsonNode)id.ToString())]),
            ["leftOut"] = new JsonArray([.. leftOut.Select(static line => (JsonNode)line)])
        };
        var payload = json.ToJsonString();
        return new RequestIntent(PartialUndoKind, payload, Hashes.Of(payload));
    }

    public static (Ulid Request, IReadOnlyList<Ulid> Include, IReadOnlyList<string> LeftOut) ReadPartialUndo(string payload)
    {
        var json = JsonNode.Parse(payload)!.AsObject();
        return (Ulid.Parse(json["request"]!.ToString()),
            [.. (json["include"]?.AsArray() ?? []).Select(static id => Ulid.Parse(id!.ToString()))],
            [.. (json["leftOut"]?.AsArray() ?? []).Select(static line => line!.ToString())]);
    }

    /// <summary>Whether the payload is what the hash was made of: the substitution check (AG-3d, N-12).</summary>
    public static bool IsIntact(RequestIntent intent) =>
        intent.Kind is not PlacementKind && string.Equals(Hashes.Of(intent.Payload), intent.Hash, StringComparison.Ordinal);
}
