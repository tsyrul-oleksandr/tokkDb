namespace TokkDb.LLM.Core;

/// <summary>
/// Stores conversations and their events.
///
/// The chat UI depends only on this abstraction, so a persistent implementation
/// can replace the in-memory one later without any UI change.
/// </summary>
public interface IConversationHistoryService
{
    /// <summary>Conversations, most recently updated first.</summary>
    IReadOnlyList<StoredConversation> GetConversations();

    StoredConversation? GetConversation(string conversationId);

    StoredConversation Create(string? title = null);

    /// <summary>
    /// Adds an event, or replaces the existing event with the same
    /// <see cref="ConversationEntry.Id"/> while keeping its position and
    /// original timestamp. Refreshes the conversation's UpdatedAt.
    /// </summary>
    StoredConversation? Append(string conversationId, ConversationEntry entry);

    bool Rename(string conversationId, string title);

    bool Delete(string conversationId);
}
