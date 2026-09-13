namespace TokkDb.Assistant.Trace;

/// <summary>
/// Where a request has got to (D-15).
///
/// A request is a resumable state machine and not only a record of what happened. D-7 and D-14
/// both pause one mid-flight to ask a person something, so the pause is a normal state rather
/// than an exception - and a crash during one otherwise leaves "asked" with nothing able to act
/// on the answer.
/// </summary>
public enum RequestState
{
    /// <summary>Work is being done. A request found in this state after a restart was interrupted.</summary>
    Running = 1,

    /// <summary>
    /// Stopped, holding the validated, resolved intent - never the conversation. Answering it
    /// executes exactly what was shown, without paying for a model again and without the risk of
    /// a second answer differing from the first.
    /// </summary>
    WaitingForUser,

    /// <summary>The answer arrived and the work it settled is being done.</summary>
    Resuming,

    /// <summary>Finished.</summary>
    Completed,

    /// <summary>Stopped because somebody said so.</summary>
    Cancelled,

    /// <summary>Stopped because something went wrong. The reason is on the trace.</summary>
    Failed
}

/// <summary>
/// One request the assistant handled, from what a person said to what it did (TR-1, D-8).
///
/// The diagram is the product feature and the trace is the diagnostic record and the token-budget
/// evidence, and they are the same thing - which is why this is one type and not three.
///
/// <b>Prunable</b> (TR-2). This and its steps are diagnostics: larger, and removable after a
/// stated retention. What is not removable is the <see cref="DataChange"/> record, which is the
/// audit log and the undo log, and which is joined to this by <see cref="Id"/> alone. After a
/// purge the interface renders "the diagram for this change is no longer kept" rather than
/// failing on a dangling identifier.
/// </summary>
public sealed record RequestTrace(
    Ulid Id,
    Ulid ConversationId,
    string Operation,
    RequestState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    string? Reason = null)
{
    /// <summary>
    /// How many times the request has moved from one state to another, so that a transition can
    /// be a compare-and-swap rather than a write that quietly wins a race (AG-8a).
    /// </summary>
    public int Transitions { get; init; }

    public bool IsFinished => State is RequestState.Completed or RequestState.Cancelled or RequestState.Failed;

    public TimeSpan? Took => EndedAt is { } ended ? ended - StartedAt : null;
}
