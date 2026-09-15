using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Requests;

/// <summary>
/// The request as a resumable state machine (D-15, AG-8, AG-8a, step 3.4).
///
/// Six states, and the edges between them are the whole of what a request can do:
///
/// <code>
/// Running        -> WaitingForUser | Completed | Cancelled | Failed
/// WaitingForUser -> Resuming (the answer was yes) | Completed (the answer was no) | Cancelled
/// Resuming       -> WaitingForUser (a second question, AG-11b) | Completed | Cancelled | Failed
/// </code>
///
/// Every move is a compare-and-swap on the request's transition counter, made by the recorder
/// (AG-8a): two answers to the same confirmation both try to move it out of
/// <see cref="RequestState.WaitingForUser"/>, and the counter lets exactly one of them. The
/// counter rather than the state, because a check on the state alone would let the second one
/// through. A move that the state machine does not allow is a programming error and throws;
/// a move somebody else made first is an ordinary outcome and returns null.
///
/// <b>What <see cref="RequestState.WaitingForUser"/> holds is the validated, resolved intent,
/// never the conversation</b> (D-15). A yes then executes exactly what was shown, without paying
/// for a model again and without the risk of a second answer differing from the first; and the
/// content hash of that intent (AG-3d) is the idempotency key of the resolved action (AG-8a), so
/// an action that already committed is recognised rather than repeated.
/// </summary>
public sealed class RequestLifecycle
{
    private static readonly IReadOnlyDictionary<RequestState, RequestState[]> Allowed =
        new Dictionary<RequestState, RequestState[]>
        {
            [RequestState.Running] = [RequestState.WaitingForUser, RequestState.Completed, RequestState.Cancelled, RequestState.Failed],
            [RequestState.WaitingForUser] = [RequestState.Resuming, RequestState.Completed, RequestState.Cancelled],
            [RequestState.Resuming] = [RequestState.WaitingForUser, RequestState.Completed, RequestState.Cancelled, RequestState.Failed],
            [RequestState.Completed] = [],
            [RequestState.Cancelled] = [],
            [RequestState.Failed] = []
        };

    private readonly ITraceRecorder _recorder;

    public RequestLifecycle(ITraceRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    public ITraceRecorder Recorder => _recorder;

    /// <summary>Starts a request, durably (TR-4a): a kill after this leaves a request that can be reasoned about.</summary>
    public RequestTrace Begin(Ulid conversationId, string operation) => _recorder.Begin(conversationId, operation);

    /// <summary>
    /// Stops to ask, holding what will be done if the answer is yes (D-15). Durable before this
    /// returns, which is what lets N-9 restore the question after a kill.
    /// </summary>
    public RequestTrace? Hold(RequestTrace request, RequestIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Move(request, RequestState.WaitingForUser, intent: intent);
    }

    /// <summary>
    /// The answer was yes: claims the request for the work it was waiting to do. A second answer
    /// to the same question arrives with the same expectation and loses (AG-8a).
    /// </summary>
    public RequestTrace? Claim(RequestTrace request) => Move(request, RequestState.Resuming);

    /// <summary>
    /// The answer was no (N-8): nothing happens, the request completes, and the refusal is in the
    /// trace as the reason.
    /// </summary>
    public RequestTrace? Decline(RequestTrace request, string reason = "declined by the user") =>
        Move(request, RequestState.Completed, reason);

    /// <summary>
    /// Finishes the request. <paramref name="committedHash"/> is the hash of the resolved action
    /// that was applied, where one was, so that a replay is recognised (AG-8a).
    /// </summary>
    public RequestTrace? Complete(RequestTrace request, string? committedHash = null, string? reason = null) =>
        Move(request, RequestState.Completed, reason, committedHash: committedHash);

    public RequestTrace? Cancel(RequestTrace request, string reason = "cancelled by the user") =>
        Move(request, RequestState.Cancelled, reason);

    public RequestTrace? Fail(RequestTrace request, string reason) =>
        Move(request, RequestState.Failed, reason);

    /// <summary>
    /// Records that the resolved action committed, without finishing the request: the write
    /// happened, and whatever asks again gets a no-op (AG-8a). Used when a request goes on to a
    /// second question after applying something.
    /// </summary>
    public RequestTrace? Committed(RequestTrace request, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return _recorder.Move(request.Id, request.State, request.Transitions, committedHash: hash);
    }

    /// <summary>Whether the state machine allows this move, whatever the counter says.</summary>
    public static bool Allows(RequestState from, RequestState to) => Allowed[from].Contains(to);

    private RequestTrace? Move(
        RequestTrace request,
        RequestState to,
        string? reason = null,
        RequestIntent? intent = null,
        string? committedHash = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Allows(request.State, to))
        {
            throw new InvalidOperationException(
                $"A request in {request.State} cannot move to {to}. Request {request.Id} ({request.Operation}).");
        }

        return _recorder.Move(request.Id, to, request.Transitions, reason, intent, committedHash);
    }
}
