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

/// <summary>
/// How long each class is kept, and the one rule that ties them together (TR-7, TR-8, AJ-8 of
/// the versioning plan): <b>the engine's version history for the assistant's collections is
/// purged no earlier than the start of the compensation window</b>, so that the window can never
/// promise an undo whose versions are gone. A configuration that would purge inside the window
/// is refused, naming both moments, rather than quietly honoured.
/// </summary>
public sealed record RetentionWindows(
    TimeSpan Diagnostics,
    TimeSpan Changes,
    TimeSpan Compensation,
    TimeSpan History)
{
    /// <summary>
    /// Thirty days of diagnostics, a year of changes, ninety days in which a request can still
    /// be undone, and history kept for the same ninety days - the least that keeps the promise.
    /// </summary>
    public static readonly RetentionWindows Default = new(
        Diagnostics: TimeSpan.FromDays(30),
        Changes: TimeSpan.FromDays(365),
        Compensation: TimeSpan.FromDays(90),
        History: TimeSpan.FromDays(90));

    /// <summary>The moment before which diagnostics may go, as of <paramref name="now"/>.</summary>
    public DateTimeOffset PurgeDiagnosticsBefore(DateTimeOffset now) => now - Diagnostics;

    /// <summary>The moment the compensation window starts: a request that finished before it can no longer be undone.</summary>
    public DateTimeOffset CompensationWindowStart(DateTimeOffset now) => now - Compensation;

    /// <summary>
    /// The moment before which version history may be purged, as of <paramref name="now"/>:
    /// never later than the start of the compensation window (AJ-8).
    /// </summary>
    /// <exception cref="RetentionConflictException">The history window is shorter than the compensation window.</exception>
    public DateTimeOffset PurgeHistoryBefore(DateTimeOffset now)
    {
        var purge = now - History;
        var window = CompensationWindowStart(now);

        if (purge > window)
        {
            throw new RetentionConflictException(purge, window);
        }

        return purge;
    }
}

/// <summary>AJ-8: the history purge would reach inside the compensation window. Both moments are named.</summary>
public sealed class RetentionConflictException : Exception
{
    public RetentionConflictException(DateTimeOffset purgeHistoryBefore, DateTimeOffset compensationWindowStart)
        : base($"History would be purged before {purgeHistoryBefore:O}, which is later than the start of the compensation " +
               $"window at {compensationWindowStart:O}: an undo inside the window could find its versions gone.")
    {
        PurgeHistoryBefore = purgeHistoryBefore;
        CompensationWindowStart = compensationWindowStart;
    }

    public DateTimeOffset PurgeHistoryBefore { get; }
    public DateTimeOffset CompensationWindowStart { get; }
}
