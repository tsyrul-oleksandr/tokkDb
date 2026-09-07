using System.Globalization;
using System.Text.Json;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.LLM.Core;
using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// CX-2: a conversation and its events as documents in <c>_conversations</c> and
/// <c>_conversationEntries</c>.
///
/// One document per event rather than one per conversation. A conversation grows for as long
/// as it is used, and appending to it must not mean rewriting everything said so far — which
/// a single document would, once per turn, for the whole length of the conversation.
///
/// What a conversation is ordered and searched by — its title, when it was started, when it
/// was last touched, which conversation an event belongs to and where in it — is written as
/// fields. The three rich payloads an event may carry are written as JSON: a tool call, a
/// workflow step and a set of records are the application's shapes, they change with the UI
/// that renders them, and nothing asks the database a question about what is inside them.
/// </summary>
public static class ConversationDocuments
{
    public const string IdField = "id";
    public const string TitleField = "title";
    public const string CreatedAtField = "createdAt";
    public const string UpdatedAtField = "updatedAt";

    public const string ConversationField = "conversation";
    public const string OrdinalField = "ordinal";
    public const string KindField = "kind";
    public const string TimestampField = "timestamp";
    public const string TextField = "text";
    public const string ToolField = "tool";
    public const string WorkflowField = "workflow";
    public const string RecordsField = "records";

    private static readonly JsonSerializerOptions Payload = new() { WriteIndented = false };

    public static List<ColumnDescriptor> CreateConversationColumns() =>
    [
        new(IdField, ValueTypeEnum.String, "Identifier the application knows the conversation by",
            unique: true),
        new(TitleField, ValueTypeEnum.String, "Title, taken from the first user message"),
        new(CreatedAtField, ValueTypeEnum.String, "When the conversation was started"),
        new(UpdatedAtField, ValueTypeEnum.String, "When it was last added to")
    ];

    public static List<ColumnDescriptor> CreateEntryColumns() =>
    [
        new(IdField, ValueTypeEnum.String, "Natural key of the event: message, call or segment id"),
        new(ConversationField, ValueTypeEnum.String, "The conversation the event belongs to"),
        new(OrdinalField, ValueTypeEnum.Int, "Position within the conversation"),
        new(KindField, ValueTypeEnum.String, "Who or what the event came from"),
        new(TimestampField, ValueTypeEnum.String, "When the event happened"),
        new(TextField, ValueTypeEnum.String, "Text of a user, assistant, system or reasoning event"),
        new(ToolField, ValueTypeEnum.String, "Tool call, as JSON"),
        new(WorkflowField, ValueTypeEnum.String, "Workflow step, as JSON"),
        new(RecordsField, ValueTypeEnum.String, "Records shown to the reader, as JSON")
    ];

    public static ObjectDocument WriteConversation(
        Ulid recordId, string id, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(recordId));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new StringDocumentValue(id),
            [TitleField] = new StringDocumentValue(title),
            [CreatedAtField] = new StringDocumentValue(Moment(createdAt)),
            [UpdatedAtField] = new StringDocumentValue(Moment(updatedAt))
        }));
        return document;
    }

    public static (string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
        ReadConversation(ObjectDocument document)
    {
        var value = (ObjectDocumentValue)document.Value;
        return (
            Text(value, IdField),
            Text(value, TitleField) is { Length: > 0 } title ? title : StoredConversation.UntitledConversation,
            Instant(Text(value, CreatedAtField)),
            Instant(Text(value, UpdatedAtField)));
    }

    public static ObjectDocument WriteEntry(
        Ulid recordId, string conversationId, int ordinal, ConversationEntry entry)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(recordId));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new StringDocumentValue(entry.Id),
            [ConversationField] = new StringDocumentValue(conversationId),
            [OrdinalField] = new IntDocumentValue(ordinal),
            //The name rather than the number, so renumbering the enum cannot silently turn a
            //tool call into a user message in an existing database.
            [KindField] = new StringDocumentValue(entry.Kind.ToString()),
            [TimestampField] = new StringDocumentValue(Moment(entry.Timestamp)),
            [TextField] = new StringDocumentValue(entry.Text ?? string.Empty),
            [ToolField] = new StringDocumentValue(Json(entry.Tool)),
            [WorkflowField] = new StringDocumentValue(Json(entry.Workflow)),
            [RecordsField] = new StringDocumentValue(Json(entry.Records))
        }));
        return document;
    }

    public static (string ConversationId, int Ordinal, ConversationEntry Entry) ReadEntry(ObjectDocument document)
    {
        var value = (ObjectDocumentValue)document.Value;
        var entry = new ConversationEntry
        {
            Id = Text(value, IdField),
            Kind = Enum.TryParse<ConversationEntryKind>(Text(value, KindField), out var kind)
                ? kind
                : ConversationEntryKind.System,
            Timestamp = Instant(Text(value, TimestampField)),
            Text = Text(value, TextField) is { Length: > 0 } text ? text : null,
            Tool = FromJson<AgentToolExecution>(Text(value, ToolField)),
            Workflow = FromJson<ConversationWorkflowEntry>(Text(value, WorkflowField)),
            Records = FromJson<RecordsDisplayMessage>(Text(value, RecordsField))
        };
        return (Text(value, ConversationField), Number(value, OrdinalField), entry);
    }

    //Round-trip format and invariant culture, so a conversation written on one machine reads
    //the same on another.
    private static string Moment(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static string Json<T>(T? value) where T : class =>
        value is null ? string.Empty : JsonSerializer.Serialize(value, Payload);

    // A payload the current application cannot read is dropped rather than taking the event
    // with it: a conversation is a log, and one unreadable tool call must not cost the rest.
    private static T? FromJson<T>(string value) where T : class
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value, Payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;

    private static int Number(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is IntDocumentValue number ? number.Value : 0;
}
