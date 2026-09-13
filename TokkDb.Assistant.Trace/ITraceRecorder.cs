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
/// Nothing here has an implementation yet: step 3.0 creates the model and the interface, and 3.2
/// and 3.3 give them their two retentions and their two durability points.
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
}
