using Microsoft.Extensions.Logging;

namespace TokkDb.LLM.Core;

/// <summary>
/// Keeps conversations in application memory only. Nothing is written to disk or
/// to a database; everything is lost when the application exits.
///
/// Registered as a singleton so every part of the application sees the same
/// history. All access is guarded, because entries arrive from the orchestrator's
/// background streaming as well as from the UI thread.
/// </summary>
public sealed class InMemoryConversationHistoryService : IConversationHistoryService
{
    private readonly Dictionary<string, MutableConversation> _conversations = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly ILogger<InMemoryConversationHistoryService> _logger;

    public InMemoryConversationHistoryService(ILogger<InMemoryConversationHistoryService> logger)
    {
        _logger = logger;
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
        var conversation = new MutableConversation
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? StoredConversation.UntitledConversation : title.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_sync)
        {
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
                string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal));

            if (existing >= 0)
            {
                // Keep the original position and timestamp: a tool call moving
                // from Started to Completed must not jump to the end.
                conversation.Entries[existing] = entry with
                {
                    Timestamp = conversation.Entries[existing].Timestamp
                };
            }
            else
            {
                conversation.Entries.Add(entry);
                _logger.LogDebug(
                    "Conversation entry added. ConversationId: {ConversationId}, EntryKind: {EntryKind}, Entries: {EntryCount}",
                    conversationId,
                    entry.Kind,
                    conversation.Entries.Count);
            }

            conversation.UpdatedAt = DateTimeOffset.UtcNow;

            // The first user message names the conversation.
            if (entry.Kind == ConversationEntryKind.User &&
                string.Equals(conversation.Title, StoredConversation.UntitledConversation, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(entry.Text))
            {
                conversation.Title = BuildTitle(entry.Text);
                _logger.LogInformation(
                    "Conversation titled. ConversationId: {ConversationId}, Title: {ConversationTitle}",
                    conversationId,
                    conversation.Title);
            }

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

            conversation.Title = title.Trim();
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
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

        bool removed;
        lock (_sync)
        {
            removed = _conversations.Remove(conversationId);
        }

        if (removed)
        {
            _logger.LogInformation("Conversation deleted. ConversationId: {ConversationId}", conversationId);
        }
        else
        {
            _logger.LogWarning(
                "Conversation delete ignored, not found. ConversationId: {ConversationId}",
                conversationId);
        }

        return removed;
    }

    private const int MaxTitleLength = 60;

    private static string BuildTitle(string text)
    {
        var normalized = text.Trim().ReplaceLineEndings(" ");
        return normalized.Length <= MaxTitleLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaxTitleLength).TrimEnd(), "...");
    }

    private sealed class MutableConversation
    {
        public required string Id { get; init; }

        public required string Title { get; set; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; set; }

        public List<ConversationEntry> Entries { get; } = [];

        public StoredConversation ToSnapshot() =>
            new(Id, Title, CreatedAt, UpdatedAt, Entries.ToArray());
    }
}
