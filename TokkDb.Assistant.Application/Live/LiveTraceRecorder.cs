using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Application.Live;

/// <summary>
/// The recorder the application hands the orchestrator: the storage's own, with every step and
/// every transition announced as it is written, so that the diagram grows while the request is
/// running (TR-5) and the reply shows what is happening (UI-5). Nothing is recorded twice and
/// nothing is recorded differently; the events are the only addition.
/// </summary>
public sealed class LiveTraceRecorder : ITraceRecorder
{
    private readonly ITraceRecorder _inner;

    public LiveTraceRecorder(ITraceRecorder inner)
    {
        _inner = inner;
    }

    public event Action<ExecutionStep>? StepRecorded;
    public event Action<RequestTrace>? RequestMoved;

    public RequestTrace Begin(Ulid conversationId, string operation)
    {
        var request = _inner.Begin(conversationId, operation);
        RequestMoved?.Invoke(request);
        return request;
    }

    public RequestTrace? Move(Ulid requestId, RequestState to, int expectedTransitions, string? reason = null, RequestIntent? intent = null, string? committedHash = null)
    {
        var moved = _inner.Move(requestId, to, expectedTransitions, reason, intent, committedHash);
        if (moved is not null) RequestMoved?.Invoke(moved);
        return moved;
    }

    public RequestTrace? Request(Ulid requestId) => _inner.Request(requestId);

    public IReadOnlyList<RequestTrace> Unfinished() => _inner.Unfinished();

    public void Record(ExecutionStep step)
    {
        _inner.Record(step);
        StepRecorded?.Invoke(step);
    }

    public int Interrupt(Ulid requestId) => _inner.Interrupt(requestId);

    public void Record(DataChange change) => _inner.Record(change);

    public (RequestTrace Request, IReadOnlyList<ExecutionStep> Steps)? Read(Ulid requestId) => _inner.Read(requestId);

    public IReadOnlyList<DataChange> Changes(Ulid requestId) => _inner.Changes(requestId);

    public IReadOnlyList<DataChange> ChangesBefore(DateTimeOffset moment) => _inner.ChangesBefore(moment);

    public int ClearPayloadsNaming(Ulid recordId) => _inner.ClearPayloadsNaming(recordId);

    public int PurgeDiagnostics(DateTimeOffset moment) => _inner.PurgeDiagnostics(moment);
}
