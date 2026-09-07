using Microsoft.Extensions.Logging;
using TokkDb.LLM.Core;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using EngineConnection = TokkDb.TokkDbConnection;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// CX-2: conversations kept in the database rather than in application memory.
///
/// It replaces <c>InMemoryConversationHistoryService</c>, whose own summary said everything is
/// lost when the application exits. The shape of what it holds is unchanged — the chat UI
/// depends on <see cref="IConversationHistoryService"/> and nothing above it moves.
///
/// The conversations are still held in memory and read from there. That is not the in-memory
/// service in disguise: what changed is that every mutation is written through to the database
/// in the same call, so the memory is a cache of something durable rather than the only copy.
/// It is a cache because the UI asks for the whole list with every event and a chat log is
/// small; a history large enough to be worth paging is a different problem from this one.
/// </summary>
public sealed class TokkDbConversationHistoryService : IConversationHistoryService
{
    private const int MaxTitleLength = 60;

    private readonly EngineConnection _connection;
    private readonly ILogger<TokkDbConversationHistoryService> _logger;
    private readonly object _sync = new();

    private readonly Dictionary<string, StoredState> _conversations = new(StringComparer.Ordinal);

    public TokkDbConversationHistoryService(
        EngineConnection connection,
        ILogger<TokkDbConversationHistoryService> logger)
    {
        _connection = connection;
        _logger = logger;
        _connection.DescribeSystemCollection(
            SystemCollections.Conversations, ConversationDocuments.CreateConversationColumns());
        _connection.DescribeSystemCollection(
            SystemCollections.ConversationEntries, ConversationDocuments.CreateEntryColumns());
        Load();
    }

    public IReadOnlyList<StoredConversation> GetConversations()
    {
        lock (_sync)
        {
            return _conversations.Values
                .OrderByDescending(conversation => conversation.UpdatedAt)
                .Select(conversation => conversation.ToSnapshot())
                .ToArray();
        }
    }

    public StoredConversation? GetConversation(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        lock (_sync)
        {
            return _conversations.TryGetValue(conversationId, out var conversation)
                ? conversation.ToSnapshot()
                : null;
        }
    }

    public StoredConversation Create(string? title = null)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new StoredState
        {
            Id = Guid.NewGuid().ToString("N"),
            RecordId = RecordIdentity.Next(),
            Title = string.IsNullOrWhiteSpace(title) ? StoredConversation.UntitledConversation : title.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_sync)
        {
            //Written first: a conversation the caller is handed and the database has not heard
            //of would be gone at the next start, and the caller has no way to find that out.
            SaveConversation(conversation);
            _conversations[conversation.Id] = conversation;
        }

        _logger.LogInformation(
            "Conversation created. ConversationId: {ConversationId}, Title: {ConversationTitle}",
            conversation.Id,
            conversation.Title);

        return conversation.ToSnapshot();
    }

    public StoredConversation? Append(string conversationId, ConversationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(entry.Id))
        {
            _logger.LogWarning(
                "Conversation entry ignored, incomplete. ConversationId: {ConversationId}, EntryId: {EntryId}",
                conversationId,
                entry.Id);
            return null;
        }

        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                _logger.LogWarning(
                    "Conversation entry ignored, unknown conversation. ConversationId: {ConversationId}, EntryKind: {EntryKind}",
                    conversationId,
                    entry.Kind);
                return null;
            }

            var existing = conversation.Entries.FindIndex(candidate =>
                string.Equals(candidate.Entry.Id, entry.Id, StringComparison.Ordinal));

            // Worked out before anything changes, because the write comes first: a cache that
            // moved ahead of a write that then failed would answer with an event the database
            // does not have, and go on doing so until the next restart disagreed with it.
            var replacing = existing >= 0;
            // Keeping the original position and timestamp is what makes a tool call moving from
            // Started to Completed one event rather than two.
            var stored = replacing
                ? entry with { Timestamp = conversation.Entries[existing].Entry.Timestamp }
                : entry;
            var ordinal = replacing ? existing : conversation.Entries.Count;
            var recordId = replacing ? conversation.Entries[existing].RecordId : RecordIdentity.Next();
            var updatedAt = DateTimeOffset.UtcNow;
            // The first user message names the conversation.
            var title = entry.Kind == ConversationEntryKind.User
                && string.Equals(conversation.Title, StoredConversation.UntitledConversation, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(entry.Text)
                    ? BuildTitle(entry.Text)
                    : conversation.Title;

            // The event and the conversation's own document move together: a conversation whose
            // UpdatedAt did not follow its last event would sort wrongly after a restart, and an
            // event whose conversation is not there could not be shown at all.
            _connection.InTransaction(() =>
            {
                _connection.SystemDocuments.Write(SystemCollections.ConversationEntries, recordId,
                    ConversationDocuments.WriteEntry(recordId, conversationId, ordinal, stored));
                _connection.SystemDocuments.Write(SystemCollections.Conversations, conversation.RecordId,
                    ConversationDocuments.WriteConversation(
                        conversation.RecordId, conversation.Id, title, conversation.CreatedAt, updatedAt));
            });

            if (replacing)
            {
                conversation.Entries[existing] = (recordId, stored);
            }
            else
            {
                conversation.Entries.Add((recordId, stored));
                _logger.LogDebug(
                    "Conversation entry added. ConversationId: {ConversationId}, EntryKind: {EntryKind}, Entries: {EntryCount}",
                    conversationId,
                    entry.Kind,
                    conversation.Entries.Count);
            }

            if (!string.Equals(title, conversation.Title, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Conversation titled. ConversationId: {ConversationId}, Title: {ConversationTitle}",
                    conversationId,
                    title);
            }

            conversation.Title = title;
            conversation.UpdatedAt = updatedAt;

            return conversation.ToSnapshot();
        }
    }

    public bool Rename(string conversationId, string title)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                return false;
            }

            var renamed = title.Trim();
            var updatedAt = DateTimeOffset.UtcNow;
            _connection.InTransaction(() => _connection.SystemDocuments.Write(
                SystemCollections.Conversations, conversation.RecordId,
                ConversationDocuments.WriteConversation(
                    conversation.RecordId, conversation.Id, renamed, conversation.CreatedAt, updatedAt)));
            conversation.Title = renamed;
            conversation.UpdatedAt = updatedAt;
        }

        _logger.LogInformation(
            "Conversation renamed. ConversationId: {ConversationId}, Title: {ConversationTitle}",
            conversationId,
            title);

        return true;
    }

    public bool Delete(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                _logger.LogWarning(
                    "Conversation delete ignored, not found. ConversationId: {ConversationId}",
                    conversationId);
                return false;
            }

            //One transaction, so a conversation cannot survive its events or the other way
            //round. Entries first: what an interrupted delete must not leave is an event whose
            //conversation is gone, because nothing would ever look for it again.
            _connection.InTransaction(() =>
            {
                foreach (var (recordId, _) in conversation.Entries)
                {
                    _connection.SystemDocuments.Delete(SystemCollections.ConversationEntries, recordId);
                }

                _connection.SystemDocuments.Delete(SystemCollections.Conversations, conversation.RecordId);
            });
            _conversations.Remove(conversationId);
        }

        _logger.LogInformation("Conversation deleted. ConversationId: {ConversationId}", conversationId);
        return true;
    }

    /// <summary>
    /// Read once, at start. The events are put back in the order they were appended in rather
    /// than the order they come off the pages, which a rewritten event changes.
    /// </summary>
    private void Load()
    {
        var entries = new List<(string ConversationId, int Ordinal, Ulid RecordId, ConversationEntry Entry)>();
        foreach (var (recordId, document) in
                 _connection.SystemDocuments.ReadAll(SystemCollections.ConversationEntries))
        {
            var (conversationId, ordinal, entry) = ConversationDocuments.ReadEntry(document);
            entries.Add((conversationId, ordinal, recordId, entry));
        }

        foreach (var (recordId, document) in
                 _connection.SystemDocuments.ReadAll(SystemCollections.Conversations))
        {
            var (id, title, createdAt, updatedAt) = ConversationDocuments.ReadConversation(document);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var conversation = new StoredState
            {
                Id = id, RecordId = recordId, Title = title, CreatedAt = createdAt, UpdatedAt = updatedAt
            };
            conversation.Entries.AddRange(entries
                .Where(candidate => string.Equals(candidate.ConversationId, id, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.Ordinal)
                .Select(candidate => (candidate.RecordId, candidate.Entry)));
            _conversations[id] = conversation;
        }

        _logger.LogInformation(
            "Conversation history loaded. Conversations: {ConversationCount}, Entries: {EntryCount}",
            _conversations.Count,
            entries.Count);
    }

    private void SaveConversation(StoredState conversation)
    {
        _connection.InTransaction(() => _connection.SystemDocuments.Write(
            SystemCollections.Conversations,
            conversation.RecordId,
            ConversationDocuments.WriteConversation(
                conversation.RecordId,
                conversation.Id,
                conversation.Title,
                conversation.CreatedAt,
                conversation.UpdatedAt)));
    }

    private static string BuildTitle(string text)
    {
        var normalized = text.Trim().ReplaceLineEndings(" ");
        return normalized.Length <= MaxTitleLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaxTitleLength).TrimEnd(), "...");
    }

    /// <summary>
    /// A conversation as this service holds it: the snapshot the interface hands out, plus the
    /// record identities its documents are stored under so an edit replaces rather than adds.
    /// </summary>
    private sealed class StoredState
    {
        public required string Id { get; init; }

        public required Ulid RecordId { get; init; }

        public required string Title { get; set; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; set; }

        public List<(Ulid RecordId, ConversationEntry Entry)> Entries { get; } = [];

        public StoredConversation ToSnapshot() =>
            new(Id, Title, CreatedAt, UpdatedAt, Entries.Select(entry => entry.Entry).ToArray());
    }
}
