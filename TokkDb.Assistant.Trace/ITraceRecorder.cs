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
    /// a second answer to the same confirmation cannot quietly win (AG-8a).
    /// </summary>
    /// <returns>The request as it now is, or null if somebody else moved it first.</returns>
    RequestTrace? Move(Ulid requestId, RequestState to, int expectedTransitions, string? reason = null);

    /// <summary>Records a step as it happens. Durable within a bounded delay (TR-4c).</summary>
    void Record(ExecutionStep step);

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
