namespace TokkDb.Assistant.Storage;

/// <summary>Who said a thing.</summary>
public enum TurnSpeaker
{
    /// <summary>The person using the application.</summary>
    Person = 1,

    /// <summary>The assistant.</summary>
    Assistant
}

/// <summary>
/// A conversation, as a stored thing.
///
/// SC-10. Four requirements depended on conversations existing - section 1.1 promises them in the
/// same file, S-7 asserts they all come back, UI-1 lists them, TR-4d says the conversation
/// continues after a reopen - and none of them defined one. This is the definition: a title, when
/// it started, when it was last used, and how many turns it holds.
///
/// <b>Deleting a conversation does not delete what it stored.</b> That is the whole distinction
/// worth stating: a conversation is a record of what was said, not a container for what was kept.
/// Someone who clears their chat history has not asked to lose a year of expenses, and a storage
/// that treated the two as one would be the most expensive misunderstanding in the application.
/// The requests it started stay, their changes stay, and the interface says so.
/// </summary>
public sealed record Conversation(
    Ulid Id,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivity,
    int TurnCount)
{
    public override string ToString() => $"{Title} ({TurnCount} turns)";
}

/// <summary>
/// One thing said, by one side, at one time.
///
/// Attachments are held <b>by reference</b> rather than by content: a turn that carried a
/// fifty-thousand-row spreadsheet records where the file was, not the file. D-8 gives the reason
/// in the trace's own terms - storing the content here would put the user's data in the database
/// twice - and it applies with more force to a conversation, which is read on every open.
/// </summary>
public sealed record ConversationTurn(
    Ulid Id,
    Ulid ConversationId,
    TurnSpeaker Speaker,
    string Text,
    IReadOnlyList<string> Attachments,
    Ulid? RequestId,
    DateTimeOffset At)
{
    /// <summary>The request this turn started, or null for a turn that asked for nothing.</summary>
    public bool StartedARequest => RequestId is not null;

    /// <summary>
    /// What the turn carried besides its text, where it carried something the conversation has
    /// to keep: the handle of the result a reply showed (QR-3a), so that "how many of those" two
    /// turns later still resolves after a restart. Text, written and read by the application;
    /// the storage keeps it and interprets none of it. Null for a turn that carried nothing.
    /// </summary>
    public string? Payload { get; init; }
}

/// <summary>
/// Where conversations live (SC-10).
///
/// A face of its own rather than a dozen more members on <see cref="IStorage"/>. The two have
/// nothing to do with each other beyond sharing a file: a conversation has no columns, no
/// definition and no queries, and putting its lifecycle next to the schema operations would make
/// both harder to read for no gain.
///
/// The identities are <see cref="Ulid"/>s for the reason records' are (SC-4): they sort by
/// creation time, so "the turns of this conversation, in the order they were said" is the order
/// they already have, and a turn can refer to the request it started before that request has
/// been written.
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// Starts a conversation. Created on the first turn rather than when a window opens, so that
    /// a person who opens the application and closes it again has not created anything.
    /// </summary>
    Conversation Start(string? title = null);

    /// <summary>That conversation, or null if there is none.</summary>
    Conversation? Get(Ulid id);

    /// <summary>
    /// Every conversation, <b>most recently active first</b>. That is a promise, unlike
    /// <see cref="IStorage.GetAll"/>'s, because UI-1 lists them in exactly this order and a list
    /// that reordered itself between two openings would be unusable.
    /// </summary>
    IReadOnlyList<Conversation> All();

    /// <summary>Renames one, returning false if there is none. Changes nothing else about it.</summary>
    bool Rename(Ulid id, string title);

    /// <summary>
    /// Removes a conversation and its turns, returning false if there was none.
    ///
    /// <b>It does not remove what its requests stored.</b> See <see cref="Conversation"/>.
    /// </summary>
    bool Delete(Ulid id);

    /// <summary>
    /// Appends a turn and moves the conversation's last activity to now.
    /// </summary>
    /// <param name="payload">What the turn carried besides its text; see <see cref="ConversationTurn.Payload"/>.</param>
    /// <exception cref="UnknownConversationException">There is no such conversation.</exception>
    ConversationTurn Append(
        Ulid conversationId,
        TurnSpeaker speaker,
        string text,
        IReadOnlyList<string>? attachments = null,
        Ulid? requestId = null,
        string? payload = null);

    /// <summary>The turns of a conversation, oldest first.</summary>
    /// <exception cref="UnknownConversationException">There is no such conversation.</exception>
    IReadOnlyList<ConversationTurn> Turns(Ulid conversationId);
}
