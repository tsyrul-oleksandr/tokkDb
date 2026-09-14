namespace TokkDb.Assistant.Storage;

/// <summary>
/// What the confirmation of an erase states (AJ-7, NF-4d, NF-4d1), so that every backend's
/// caller says the same: inside the boundary are the record, every version of it, and the
/// diagnostic payloads of the requests that changed it; outside it, and left, are the user's own
/// conversations - deleted when the user deletes them - and everything beyond the database file
/// and its journal.
/// </summary>
public static class Erasure
{
    public const string ConversationsAreKept =
        "Erasing removes the record, every past version of it, and what the requests that changed it were given and " +
        "produced. Your conversations are kept: what you typed stays until you delete the conversation. Copies outside " +
        "the database - in process memory, the operating system, the file system or backups - are beyond its reach.";

    /// <summary>The confirmation text for erasing one thing, naming it.</summary>
    public static string Confirmation(string what) => $"Erase {what}? {ConversationsAreKept}";
}
