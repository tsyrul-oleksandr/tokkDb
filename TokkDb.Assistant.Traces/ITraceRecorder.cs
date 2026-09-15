namespace TokkDb.Assistant.Trace;

/// <summary>
/// Where a trace is written (D-8).
///
/// The interface is in this project and the implementation is in <c>Agents</c>, which is what
/// lets <c>Storage.Engine</c> satisfy TR-4 - committing a <see cref="DataChange"/> in the same
/// transaction as the mutation it describes - without depending on the orchestrator.
///
/// <b>Two durability rules, not one</b> (TR-4, TR-4a). The change record is inside the
/// transaction of the mutation and is atomic with it. The lifecycle and the diagnostic steps are
/// written independently as they happen, because the diagram grows while the request is running
/// and a confirmation has to survive a kill that arrives before any data write exists - neither
/// of which can sit inside a transaction held open across a model call and a human pause.
///
/// Step 3.0 created the model and the interface; step 3.2 gives them their two retentions and
/// their first implementation over the engine's reserved collections; 3.3 gives them their two
/// durability points.
/// </summary>
public interface ITraceRecorder
{
    /// <summary>Starts a request, durably: a kill after this leaves a request that can be reasoned about.</summary>
    RequestTrace Begin(Ulid conversationId, string operation);

    /// <summary>
    /// Moves a request to another state, as a compare-and-swap on its transition counter, so that
    /// a second answer to the same confirmation cannot quietly win (AG-8a). <b>Durable at every
    /// transition</b> (TR-4c): when this returns, the new state is on disk, and any diagnostic
    /// steps still waiting to be written have been written first.
    /// </summary>
    /// <param name="intent">
    /// What the request is now waiting to do, for a move to <see cref="RequestState.WaitingForUser"/>;
    /// null keeps whatever intent the request already holds.
    /// </param>
    /// <param name="committedHash">
    /// The hash of the resolved action that has just committed, recorded as the idempotency key of
    /// AG-8a; null keeps what is there.
    /// </param>
    /// <returns>The request as it now is, or null if somebody else moved it first.</returns>
    RequestTrace? Move(
        Ulid requestId,
        RequestState to,
        int expectedTransitions,
        string? reason = null,
        RequestIntent? intent = null,
        string? committedHash = null);

    /// <summary>The request as it is now, or null if there is none or its diagnostics were purged.</summary>
    RequestTrace? Request(Ulid requestId);

    /// <summary>
    /// Every request that has not finished - <see cref="RequestState.Running"/>,
    /// <see cref="RequestState.WaitingForUser"/> or <see cref="RequestState.Resuming"/> - which is
    /// what startup recovery reads first (AG-8b, TR-4b).
    /// </summary>
    IReadOnlyList<RequestTrace> Unfinished();

    /// <summary>
    /// Records a step as it happens. Durable within a bounded delay (TR-4c): immediately by
    /// default, or at the latest when the delay the recorder was configured with has passed, when
    /// the next lifecycle transition or change is recorded, when the trace is next read, or when
    /// the recorder is disposed. With a delay configured, the last step before a crash may be lost;
    /// a lifecycle transition never is.
    /// </summary>
    void Record(ExecutionStep step);

    /// <summary>
    /// Marks every step of the request that still reads as <see cref="StepStatus.Running"/> as
    /// <see cref="StepStatus.Interrupted"/> (TR-4b): the process went while it was running, and a
    /// step that never finished must never read as done.
    /// </summary>
    /// <returns>How many steps were marked.</returns>
    int Interrupt(Ulid requestId);

    /// <summary>
    /// Records a change to the data. <b>Called inside the transaction that makes the change</b>,
    /// so that neither can exist without the other (TR-4).
    /// </summary>
    void Record(DataChange change);

    /// <summary>The trace of one request, with its steps, for the diagram and the detail panel.</summary>
    (RequestTrace Request, IReadOnlyList<ExecutionStep> Steps)? Read(Ulid requestId);

    /// <summary>Every change one request made, in the order it made them - which is what an undo replays backwards.</summary>
    IReadOnlyList<DataChange> Changes(Ulid requestId);

    /// <summary>
    /// Every change made before <paramref name="moment"/>, oldest first: what the history purge
    /// of step 9.2 walks to find the records whose old versions may go, and what a report of
    /// changes reads. A change is on the journal's own window (TR-8), so this is not affected by a
    /// diagnostics purge.
    /// </summary>
    IReadOnlyList<DataChange> ChangesBefore(DateTimeOffset moment);

    /// <summary>
    /// Clears the step input and output payloads of every request whose change names the record
    /// (NF-4d1, TR-2b): the steps stay, with their names, times and statuses, so the diagram still
    /// draws; what may have quoted the record's values goes. Called inside the erase's transaction.
    /// </summary>
    /// <returns>How many steps had a payload cleared.</returns>
    int ClearPayloadsNaming(Ulid recordId);

    /// <summary>
    /// Removes the diagnostics of requests that finished before <paramref name="moment"/>, and
    /// <b>nothing else</b> (TR-7, TR-8).
    ///
    /// Not the change journal, which is what a purge is defined against: after one, every change
    /// is still attributable to a request and a time, and D-17's compensation still works inside
    /// its window. Not data, and not conversations, which are the user's and are on no window at
    /// all. And not a request that has not finished - a request can sit in
    /// <see cref="RequestState.WaitingForUser"/> for as long as the person takes, and its age
    /// says nothing about whether it is still wanted.
    ///
    /// <see cref="Read"/> then returns null for a purged request while <see cref="Changes"/>
    /// still returns its changes, which is the dangling identifier TR-2 asks the interface to
    /// render as "the diagram for this change is no longer kept" rather than to fail on.
    ///
    /// <i>The window is step 9.2's</i>: this takes a moment rather than a policy, because a
    /// retention default is a decision about how long, and this is the mechanism it will use.
    /// </summary>
    /// <returns>How many requests had their diagnostics removed.</returns>
    int PurgeDiagnostics(DateTimeOffset moment);
}
