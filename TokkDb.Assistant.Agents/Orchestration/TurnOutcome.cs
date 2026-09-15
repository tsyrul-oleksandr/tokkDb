using System.Text.Json.Nodes;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>What a person said, and what they attached (UI-3).</summary>
/// <param name="ConversationId">The conversation, or null to start one on this turn (SC-10).</param>
/// <param name="Text">What they typed.</param>
/// <param name="Attachments">Files by path, read here.</param>
/// <param name="Files">Files already read, for a caller that has them - the tests, or a paste.</param>
public sealed record TurnInput(Ulid? ConversationId, string Text, IReadOnlyList<string>? Attachments = null, IReadOnlyList<ParsedFile>? Files = null)
{
    public IReadOnlyList<string> AttachmentPaths => Attachments ?? [];
    public IReadOnlyList<ParsedFile> ReadFiles => Files ?? [];
    public bool HasAttachments => AttachmentPaths.Count > 0 || ReadFiles.Count > 0;
}

/// <summary>One record as it was shown: its identity, the version it was at, and the name it was shown under (QR-3a, QR-4b).</summary>
public sealed record ShownRecord(Ulid Id, Ulid Version, string Title);

/// <summary>
/// A result, addressable later (QR-3a, QR-3b): the query that produced it, the result metadata -
/// count, the fields present, ranges, a total - and the ordered identities of the page that was
/// displayed, never the rows. Bounded by the page shown and never by the size of the result, and
/// no identity reaches a model (D-6). It survives a restart with the conversation, as the
/// assistant turn's payload.
/// </summary>
public sealed record QueryResultHandle(
    string Thing,
    string QueryJson,
    int Count,
    IReadOnlyList<string> Fields,
    IReadOnlyDictionary<string, string> Ranges,
    IReadOnlyList<ShownRecord> Shown,
    string? TotalField,
    string? Total,
    DateTimeOffset ShownAt)
{
    public StorageQuery Query => Orchestration.QueryJson.Read(QueryJson);

    public string Write()
    {
        var json = new JsonObject
        {
            ["kind"] = "result",
            ["thing"] = Thing,
            ["query"] = QueryJson,
            ["count"] = Count,
            ["fields"] = new JsonArray([.. Fields.Select(static field => (JsonNode)field)]),
            ["ranges"] = new JsonObject(Ranges.Select(static range => new KeyValuePair<string, JsonNode?>(range.Key, range.Value))),
            ["shown"] = new JsonArray([.. Shown.Select(static record => (JsonNode)new JsonObject { ["id"] = record.Id.ToString(), ["version"] = record.Version.ToString(), ["title"] = record.Title })]),
            ["totalField"] = TotalField,
            ["total"] = Total,
            ["shownAt"] = ShownAt.ToString("O")
        };

        return json.ToJsonString();
    }

    public static QueryResultHandle? TryRead(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            var json = JsonNode.Parse(payload)?.AsObject();
            if (json is null || json["kind"]?.ToString() != "result") return null;

            return new QueryResultHandle(
                json["thing"]!.ToString(),
                json["query"]!.ToString(),
                json["count"]!.GetValue<int>(),
                [.. (json["fields"]?.AsArray() ?? []).Select(static field => field!.ToString())],
                (json["ranges"]?.AsObject() ?? []).ToDictionary(static range => range.Key, static range => range.Value?.ToString() ?? "", StringComparer.Ordinal),
                [.. (json["shown"]?.AsArray() ?? []).Select(static record => new ShownRecord(
                    Ulid.Parse(record!["id"]!.ToString()), Ulid.Parse(record["version"]!.ToString()), record["title"]?.ToString() ?? ""))],
                json["totalField"]?.ToString(),
                json["total"]?.ToString(),
                DateTimeOffset.Parse(json["shownAt"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception failure) when (failure is System.Text.Json.JsonException or FormatException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }
}

/// <summary>The records a reply shows, rendered by the application and never by a model (D-6, QR-2).</summary>
public sealed record ResultPage(
    string Thing,
    IReadOnlyList<StorageRecord> Records,
    IReadOnlyList<string> Titles,
    int Total,
    IReadOnlyDictionary<string, object?> Aggregates,
    QueryExecutionInfo Execution,
    QueryResultHandle Handle);

/// <summary>
/// What one turn came to: the reply, a question to answer first, the records to show, and the
/// request behind it with the state it reached (D-15).
/// </summary>
public sealed record TurnOutcome(
    Ulid ConversationId,
    Ulid RequestId,
    RequestState State,
    string Reply,
    ConfirmationCard? Question = null,
    ResultPage? Results = null,
    string? Failure = null)
{
    public bool IsWaiting => State is RequestState.WaitingForUser;

    public bool Succeeded => State is RequestState.Completed && Failure is null;
}
