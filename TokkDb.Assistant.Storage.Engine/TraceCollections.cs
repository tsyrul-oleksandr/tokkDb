using TokkDb.Assistant.Trace;
using TokkDb.Pages;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// Which reserved collection holds what, and for how long - the count D-2 left open, settled
/// (step 3.2).
///
/// <b>Five, three of them new, and the number comes from retention rather than from layout.</b>
/// Two things with different lifetimes cannot share a collection, because a purge removes
/// documents and a document is either removed or it is not. <see cref="RetentionClass"/> carries
/// the reasoning; this is the list it applies to, written against the engine's own names so that
/// the two cannot drift apart without a test noticing.
///
/// It is here rather than in <c>TokkDb.Assistant.Trace</c> because these are the engine's names.
/// §3.1 has the trace project depending on nothing, and a list of engine constants is a
/// dependency whichever direction it is copied in; the classes are the trace's, the names are the
/// engine's, and this is the one file that knows both.
/// </summary>
public static class TraceCollections
{
    /// <summary>
    /// The reserved collections the assistant writes to, and how long each lives.
    ///
    /// <c>_settings</c>, <c>_relations</c> and the rest of the catalogue are not here: they are
    /// the engine's, they describe the data rather than the assistant's work, and they live
    /// exactly as long as what they describe.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RetentionClass> Reserved =
        new Dictionary<string, RetentionClass>(StringComparer.Ordinal)
        {
            [SystemCollections.Traces] = RetentionClass.Diagnostic,
            [SystemCollections.TraceSteps] = RetentionClass.Diagnostic,
            [SystemCollections.DataChanges] = RetentionClass.Journal,
            [SystemCollections.Conversations] = RetentionClass.UserOwned,
            [SystemCollections.ConversationEntries] = RetentionClass.UserOwned
        };

    /// <summary>
    /// What a diagnostics purge is allowed to touch (TR-7). Everything else in
    /// <see cref="Reserved"/> is either the audit record or the user's own.
    /// </summary>
    public static IReadOnlyList<string> Prunable =>
        [.. Reserved.Where(static entry => entry.Value is RetentionClass.Diagnostic).Select(static entry => entry.Key)];

    public static RetentionClass ClassOf(string collectionName) =>
        Reserved.TryGetValue(collectionName, out var retention)
            ? retention
            : throw new ArgumentException($"'{collectionName}' is not one of the assistant's reserved collections.",
                nameof(collectionName));
}
