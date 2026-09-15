using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Requests;

/// <summary>What startup recovery did with one request it found unfinished (AG-8b).</summary>
public enum RecoveryAction
{
    /// <summary>
    /// It was <see cref="RequestState.Running"/> and nothing of it had committed: the model call
    /// it was inside is gone, so it is <see cref="RequestState.Failed"/> with a reason of
    /// interrupted.
    /// </summary>
    FailedAsInterrupted = 1,

    /// <summary>
    /// It was <see cref="RequestState.Running"/> and its change record shows the mutation
    /// committed: completed from the record, since the work was done and only the reply was lost.
    /// </summary>
    CompletedFromRecord,

    /// <summary>It was <see cref="RequestState.WaitingForUser"/>: restored exactly as it was (AG-8).</summary>
    RestoredWaiting,

    /// <summary>
    /// It was <see cref="RequestState.Resuming"/> and the action had not committed: claimed
    /// atomically, and handed back for the work to be redone from the held intent.
    /// </summary>
    ClaimedForResumption,

    /// <summary>
    /// It was <see cref="RequestState.Resuming"/> and the held action's hash is already recorded
    /// as committed: nothing to redo, so it is completed (AG-8a).
    /// </summary>
    CompletedAlreadyCommitted
}

/// <summary>One request as recovery found it and left it.</summary>
public sealed record RecoveredRequest(RequestTrace Before, RequestTrace After, RecoveryAction Action, int StepsInterrupted)
{
    /// <summary>Whether the orchestrator has work to redo for it, from <see cref="RequestTrace.Intent"/>.</summary>
    public bool NeedsResumption => Action is RecoveryAction.ClaimedForResumption;
}

/// <summary>
/// Startup recovery, defined rather than discovered (AG-8b, TR-4b, step 3.4).
///
/// Three paths, one per unfinished state, and none of them leaves a request in a state from
/// which nothing can act on it, and none re-executes a committed write:
///
/// <list type="bullet">
/// <item><b>Running</b> cannot be resumed, because the model call it was inside is gone. It becomes
/// <see cref="RequestState.Failed"/> with a reason of interrupted - unless its change record shows
/// the mutation committed, in which case it is completed from the record.</item>
/// <item><b>WaitingForUser</b> is restored as it was: the question is still there, and answering it
/// executes what was shown (AG-8).</item>
/// <item><b>Resuming</b> is claimed atomically - a compare-and-swap that moves the counter - before
/// any work is redone, so two recoveries cannot both redo it; and if the held action's hash is
/// already recorded as committed, there is nothing to redo and it is completed (AG-8a).</item>
/// </list>
///
/// Before any of that, every step of the request still reading as running is marked interrupted
/// (TR-4b): the process went while it was running, and the diagram says so rather than implying
/// a result that was never produced.
/// </summary>
public static class StartupRecovery
{
    public const string InterruptedReason = "interrupted: the application was closed while this was running";
    public const string CompletedFromRecordReason = "completed from the change record: the change had committed before the application was closed";
    public const string AlreadyCommittedReason = "completed: the action had already committed before the application was closed";

    public static IReadOnlyList<RecoveredRequest> Run(ITraceRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        var recovered = new List<RecoveredRequest>();

        foreach (var request in recorder.Unfinished())
        {
            var interrupted = recorder.Interrupt(request.Id);

            var (after, action) = request.State switch
            {
                RequestState.Running => RecoverRunning(recorder, request),
                RequestState.WaitingForUser => (request, RecoveryAction.RestoredWaiting),
                RequestState.Resuming => RecoverResuming(recorder, request),
                _ => (request, RecoveryAction.RestoredWaiting)
            };

            recovered.Add(new RecoveredRequest(request, after, action, interrupted));
        }

        return recovered;
    }

    private static (RequestTrace After, RecoveryAction Action) RecoverRunning(ITraceRecorder recorder, RequestTrace request)
    {
        var committed = recorder.Changes(request.Id).Count > 0;

        var moved = committed
            ? recorder.Move(request.Id, RequestState.Completed, request.Transitions, CompletedFromRecordReason,
                committedHash: request.Intent?.Hash)
            : recorder.Move(request.Id, RequestState.Failed, request.Transitions, InterruptedReason);

        // Somebody else moved it first - another recovery, on another thread - and theirs stands.
        return (moved ?? recorder.Request(request.Id) ?? request,
            committed ? RecoveryAction.CompletedFromRecord : RecoveryAction.FailedAsInterrupted);
    }

    private static (RequestTrace After, RecoveryAction Action) RecoverResuming(ITraceRecorder recorder, RequestTrace request)
    {
        if (request.Intent is { } intent && request.HasCommitted(intent.Hash))
        {
            var done = recorder.Move(request.Id, RequestState.Completed, request.Transitions, AlreadyCommittedReason);
            return (done ?? recorder.Request(request.Id) ?? request, RecoveryAction.CompletedAlreadyCommitted);
        }

        // The claim: the same state, one more transition. Whoever moves the counter first owns
        // the redo; a second recovery finds the counter moved and does not redo it.
        var claimed = recorder.Move(request.Id, RequestState.Resuming, request.Transitions,
            "claimed for resumption after the application was closed while it was resuming");

        return claimed is null
            ? (recorder.Request(request.Id) ?? request, RecoveryAction.RestoredWaiting)
            : (claimed, RecoveryAction.ClaimedForResumption);
    }
}
