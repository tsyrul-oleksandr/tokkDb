namespace TokkDb.Assistant.Trace;

/// <summary>
/// How long a thing the assistant writes is kept, and by whose decision (TR-2, TR-7, TR-8, SC-10).
///
/// <b>Three of them, and that is what settles the number of reserved collections D-2 left open.</b>
/// D-2 said the assistant needs new reserved collections and deliberately did not say how many,
/// because an earlier draft said two before the trace model had a change journal or a request
/// state in it. Step 3.2 is where the number is settled, and it is settled by retention rather
/// than by layout: <b>two things with different lifetimes cannot share a collection</b>, because a
/// purge removes documents and a document is either removed or it is not.
///
/// The assistant keeps five reserved collections, three of them new:
///
/// <list type="bullet">
/// <item><c>_traces</c> and <c>_traceSteps</c> - <see cref="Diagnostic"/>. Prunable on a stated
/// window (TR-7). Two rather than one for the reason <c>_conversations</c> and
/// <c>_conversationEntries</c> are two: the steps of one request are read together and are far
/// more numerous than the requests, so an array inside the request document would be rewritten
/// once per step for the length of the request.</item>
/// <item><c>_dataChanges</c> - <see cref="Journal"/>. A longer window of its own, never touched by
/// a diagnostics purge (TR-8), because it is the audit record and the undo log.</item>
/// <item><c>_conversations</c> and <c>_conversationEntries</c> - <see cref="UserOwned"/>. Already
/// reserved by the engine, and not on a window at all: they go when the user says so (SC-10).</item>
/// </list>
///
/// <b>And a request's state is not a fourth thing.</b> D-2 listed it separately and it does not
/// need a collection: it lives on the trace document, because a request that is waiting for an
/// answer is exactly the request whose diagram is being looked at (D-15). Putting it elsewhere
/// would mean two documents to keep in step for one request, and a state that could outlive the
/// steps that explain it.
///
/// <i>The windows themselves are step 9.2.</i> What is settled here is that there are three
/// classes and which collection is in which; how long a diagnostic lives, and how long a change
/// lives, are two numbers that also bound D-17's compensation window and are listed as open in
/// section 10.
/// </summary>
public enum RetentionClass
{
    /// <summary>
    /// Larger and prunable. The trace, its steps and their model calls: what the diagram is drawn
    /// from and what the token budget is measured from, neither of which is evidence of anything
    /// once it is old enough to have been purged.
    /// </summary>
    Diagnostic = 1,

    /// <summary>
    /// The change journal. Kept longer than the diagnostics that describe it, and never removed
    /// by a diagnostics purge - after which every change is still attributable to a request and a
    /// time, even though the diagram of that request is gone (TR-8).
    /// </summary>
    Journal,

    /// <summary>
    /// The user's own. Conversations are deleted when the user deletes them and on no window at
    /// all, which is the third retention and the reason two was never going to be enough.
    /// </summary>
    UserOwned
}
