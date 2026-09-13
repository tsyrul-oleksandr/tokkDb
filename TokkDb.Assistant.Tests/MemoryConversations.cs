using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// <see cref="IConversationStore"/> in dictionaries (SC-10).
///
/// The same discipline as <see cref="MemoryStorage"/>: it obeys the contract rather than
/// approximating it. Conversations come back most recently active first because the contract
/// promises that order, turns come back oldest first, and deleting a conversation deletes its
/// turns and nothing else - which is the promise the engine-backed one has to keep too, and the
/// only way the shared suite can tell.
/// </summary>
public sealed class MemoryConversations : IConversationStore
{
    private readonly Dictionary<Ulid, Conversation> _conversations = [];
    private readonly List<ConversationTurn> _turns = [];

    public Conversation Start(string? title = null)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(
            RecordIdentity.Next(),
            string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
            now,
            now,
            TurnCount: 0);

        _conversations[conversation.Id] = conversation;
        return conversation;
    }

    public Conversation? Get(Ulid id) =>
        _conversations.TryGetValue(id, out var conversation)
            ? conversation with { TurnCount = _turns.Count(turn => turn.ConversationId == id) }
            : null;

    public IReadOnlyList<Conversation> All() =>
    [
        .. _conversations.Keys
            .Select(Get)
            .OfType<Conversation>()
            .OrderByDescending(static conversation => conversation.LastActivity)
            .ThenByDescending(static conversation => conversation.Id)
    ];

    public bool Rename(Ulid id, string title)
    {
        if (!_conversations.TryGetValue(id, out var conversation)) return false;

        _conversations[id] = conversation with
        {
            Title = string.IsNullOrWhiteSpace(title) ? conversation.Title : title.Trim()
        };

        return true;
    }

    public bool Delete(Ulid id)
    {
        if (!_conversations.Remove(id)) return false;

        _turns.RemoveAll(turn => turn.ConversationId == id);
        return true;
    }

    public ConversationTurn Append(
        Ulid conversationId,
        TurnSpeaker speaker,
        string text,
        IReadOnlyList<string>? attachments = null,
        Ulid? requestId = null)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            throw new UnknownConversationException(conversationId);
        }

        var turn = new ConversationTurn(
            RecordIdentity.Next(),
            conversationId,
            speaker,
            text ?? string.Empty,
            [.. attachments ?? []],
            requestId,
            DateTimeOffset.UtcNow);

        _turns.Add(turn);
        _conversations[conversationId] = conversation with { LastActivity = turn.At };

        return turn;
    }

    public IReadOnlyList<ConversationTurn> Turns(Ulid conversationId)
    {
        if (!_conversations.ContainsKey(conversationId)) throw new UnknownConversationException(conversationId);

        return [.. _turns.Where(turn => turn.ConversationId == conversationId).OrderBy(static turn => turn.Id)];
    }
}
