using TokkDb.Assistant.Storage;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;
using EngineColumn = TokkDb.Pages.ColumnDescriptor;
using EngineConnection = TokkDb.TokkDbConnection;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// Conversations (SC-10) in the engine's reserved collections.
///
/// D-2 again, and the engine had already made the decision: <c>_conversations</c> and
/// <c>_conversationEntries</c> are reserved names that <c>Initialize</c> creates, and
/// <c>SystemDocumentStore</c> reads and writes their documents through the same pages, journal
/// and transaction as everything else. So there is no new storage mechanism here - only the
/// shape of two documents, declared through <c>DescribeSystemCollection</c> so that the
/// catalogue says what they hold rather than the knowledge living in this file alone.
///
/// <b>One document per turn, not one per conversation.</b> A conversation grows for as long as
/// it is used, and appending to it must not mean rewriting everything said so far - which a
/// single document would, once per turn, for the whole length of the conversation. That is the
/// same reason the engine split the two collections in the first place.
///
/// <b>And deleting a conversation deletes only what was said.</b> The records its requests
/// stored are ordinary records in ordinary collections and are not touched; see
/// <see cref="Conversation"/> for why that is the only defensible answer.
/// </summary>
internal sealed class EngineConversations : IConversationStore
{
    private const string IdField = "id";
    private const string TitleField = "title";
    private const string StartedField = "startedAt";
    private const string ActivityField = "lastActivity";

    private const string ConversationField = "conversation";
    private const string SpeakerField = "speaker";
    private const string TextField = "text";
    private const string AttachmentsField = "attachments";
    private const string RequestField = "request";
    private const string AtField = "at";

    // Attachments are held by reference and there are rarely more than a few, so they are one
    // field with a separator rather than a collection of their own. A file path cannot contain
    // this character on any platform the plan targets.
    private const char AttachmentSeparator = (char)31;

    private readonly EngineConnection _connection;
    private bool _described;

    public EngineConversations(EngineConnection connection)
    {
        _connection = connection;
    }

    public Conversation Start(string? title = null)
    {
        Describe();

        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(
            RecordIdentity.Next(),
            Title(title),
            now,
            now,
            TurnCount: 0);

        _connection.InTransaction(() => Write(conversation));

        return conversation;
    }

    public Conversation? Get(Ulid id)
    {
        Describe();

        foreach (var (stored, document) in _connection.SystemDocuments.ReadAll(SystemCollections.Conversations))
        {
            if (stored == id) return ToConversation(stored, document, Count(id));
        }

        return null;
    }

    public IReadOnlyList<Conversation> All()
    {
        Describe();

        var counts = Counts();

        return [.. _connection.SystemDocuments
            .ReadAll(SystemCollections.Conversations)
            .Select(entry => ToConversation(entry.Id, entry.Document, counts.GetValueOrDefault(entry.Id)))
            .OrderByDescending(static conversation => conversation.LastActivity)
            .ThenByDescending(static conversation => conversation.Id)];
    }

    public bool Rename(Ulid id, string title)
    {
        Describe();

        if (Get(id) is not { } conversation) return false;

        _connection.InTransaction(() => Write(conversation with { Title = Title(title) }));
        return true;
    }

    public bool Delete(Ulid id)
    {
        Describe();

        if (Get(id) is null) return false;

        var turns = Turns(id).Select(static turn => turn.Id).ToList();

        _connection.InTransaction(() =>
        {
            foreach (var turn in turns)
            {
                _connection.SystemDocuments.Delete(SystemCollections.ConversationEntries, turn);
            }

            _connection.SystemDocuments.Delete(SystemCollections.Conversations, id);
        });

        return true;
    }

    public ConversationTurn Append(
        Ulid conversationId,
        TurnSpeaker speaker,
        string text,
        IReadOnlyList<string>? attachments = null,
        Ulid? requestId = null)
    {
        Describe();

        var conversation = Get(conversationId) ?? throw new UnknownConversationException(conversationId);

        var turn = new ConversationTurn(
            RecordIdentity.Next(),
            conversationId,
            speaker,
            text ?? string.Empty,
            [.. attachments ?? []],
            requestId,
            DateTimeOffset.UtcNow);

        // One transaction for both, so that a conversation cannot be left saying it was last
        // used at a time no turn accounts for.
        _connection.InTransaction(() =>
        {
            _connection.SystemDocuments.Write(SystemCollections.ConversationEntries, turn.Id, ToDocument(turn));
            Write(conversation with { LastActivity = turn.At });
        });

        return turn;
    }

    public IReadOnlyList<ConversationTurn> Turns(Ulid conversationId)
    {
        Describe();

        if (Get(conversationId) is null) throw new UnknownConversationException(conversationId);

        return [.. _connection.SystemDocuments
            .ReadAll(SystemCollections.ConversationEntries)
            .Select(static entry => ToTurn(entry.Id, entry.Document))
            .Where(turn => turn.ConversationId == conversationId)
            .OrderBy(static turn => turn.Id)];
    }

    // ---- The documents ------------------------------------------------------------------------

    private void Describe()
    {
        if (_described) return;

        _connection.DescribeSystemCollection(SystemCollections.Conversations, ConversationColumns());
        _connection.DescribeSystemCollection(SystemCollections.ConversationEntries, TurnColumns());

        _described = true;
    }

    private static List<EngineColumn> ConversationColumns() =>
    [
        new(IdField, ValueTypeEnum.Ulid, "The conversation", unique: true, readOnly: true),
        new(TitleField, ValueTypeEnum.String, "What it is called"),
        new(StartedField, ValueTypeEnum.DateTime, "When the first thing was said"),
        new(ActivityField, ValueTypeEnum.DateTime, "When the last thing was said")
    ];

    private static List<EngineColumn> TurnColumns() =>
    [
        new(IdField, ValueTypeEnum.Ulid, "The turn", unique: true, readOnly: true),
        new(ConversationField, ValueTypeEnum.Ulid, "The conversation it belongs to"),
        new(SpeakerField, ValueTypeEnum.String, "Who said it"),
        new(TextField, ValueTypeEnum.String, "What was said"),
        new(AttachmentsField, ValueTypeEnum.String, "What was attached, by reference"),
        new(RequestField, ValueTypeEnum.Ulid, "The request this turn started, if it started one"),
        new(AtField, ValueTypeEnum.DateTime, "When it was said")
    ];

    private void Write(Conversation conversation)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(conversation.Id));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new UlidDocumentValue(conversation.Id),
            [TitleField] = new StringDocumentValue(conversation.Title),
            [StartedField] = new DateTimeDocumentValue(conversation.StartedAt.UtcDateTime),
            [ActivityField] = new DateTimeDocumentValue(conversation.LastActivity.UtcDateTime)
        }));

        _connection.SystemDocuments.Write(SystemCollections.Conversations, conversation.Id, document);
    }

    private static ObjectDocument ToDocument(ConversationTurn turn)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(turn.Id));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new UlidDocumentValue(turn.Id),
            [ConversationField] = new UlidDocumentValue(turn.ConversationId),
            [SpeakerField] = new StringDocumentValue(turn.Speaker.ToString()),
            [TextField] = new StringDocumentValue(turn.Text),
            [AttachmentsField] = new StringDocumentValue(string.Join(AttachmentSeparator, turn.Attachments)),
            [RequestField] = turn.RequestId is { } request
                ? new UlidDocumentValue(request)
                : new NullDocumentValue(),
            [AtField] = new DateTimeDocumentValue(turn.At.UtcDateTime)
        }));

        return document;
    }

    private static Conversation ToConversation(Ulid id, ObjectDocument document, int turns)
    {
        var value = (ObjectDocumentValue)document.Value;

        return new Conversation(
            id,
            Text(value, TitleField),
            Moment(value, StartedField),
            Moment(value, ActivityField),
            turns);
    }

    private static ConversationTurn ToTurn(Ulid id, ObjectDocument document)
    {
        var value = (ObjectDocumentValue)document.Value;
        var attachments = Text(value, AttachmentsField);

        return new ConversationTurn(
            id,
            Identity(value, ConversationField) ?? default,
            Enum.TryParse<TurnSpeaker>(Text(value, SpeakerField), out var speaker) ? speaker : TurnSpeaker.Person,
            Text(value, TextField),
            attachments.Length == 0 ? [] : attachments.Split(AttachmentSeparator),
            Identity(value, RequestField),
            Moment(value, AtField));
    }

    private int Count(Ulid conversationId) => Counts().GetValueOrDefault(conversationId);

    private Dictionary<Ulid, int> Counts()
    {
        var counts = new Dictionary<Ulid, int>();

        foreach (var (_, document) in _connection.SystemDocuments.ReadAll(SystemCollections.ConversationEntries))
        {
            if (Identity((ObjectDocumentValue)document.Value, ConversationField) is not { } conversation) continue;

            counts[conversation] = counts.GetValueOrDefault(conversation) + 1;
        }

        return counts;
    }

    private static string Text(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;

    private static Ulid? Identity(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is UlidDocumentValue id ? id.Value : null;

    private static DateTimeOffset Moment(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is DateTimeDocumentValue moment
            ? new DateTimeOffset(DateTime.SpecifyKind(moment.Value, DateTimeKind.Utc))
            : default;

    /// <summary>
    /// A conversation always has something to be called, because UI-1 lists it before anyone has
    /// renamed it. The first thing said is the title the application will normally set.
    /// </summary>
    private static string Title(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim();
}
