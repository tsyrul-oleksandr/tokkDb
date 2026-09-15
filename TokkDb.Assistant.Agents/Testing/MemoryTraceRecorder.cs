using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Testing;

/// <summary>
/// <see cref="ITraceRecorder"/> in memory, for orchestration tests against the in-memory storage
/// (D-12): the same compare-and-swap, the same purge rules, the same "interrupted" marking, so
/// that a test of the orchestrator means the same over both backends.
/// </summary>
public sealed class MemoryTraceRecorder : ITraceRecorder
{
    private readonly Dictionary<Ulid, RequestTrace> _requests = [];
    private readonly Dictionary<Ulid, ExecutionStep> _steps = [];
    private readonly List<DataChange> _changes = [];
    private readonly object _gate = new();

    public IReadOnlyCollection<RequestTrace> Requests
    {
        get { lock (_gate) return [.. _requests.Values]; }
    }

    public IReadOnlyList<DataChange> AllChanges
    {
        get { lock (_gate) return [.. _changes.OrderBy(static change => change.Id)]; }
    }

    public RequestTrace Begin(Ulid conversationId, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            var request = new RequestTrace(Ulid.NewUlid(), conversationId, operation, RequestState.Running, DateTimeOffset.UtcNow);
            _requests[request.Id] = request;
            return request;
        }
    }

    public RequestTrace? Move(
        Ulid requestId, RequestState to, int expectedTransitions, string? reason = null,
        RequestIntent? intent = null, string? committedHash = null)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var request)) return null;
            if (request.Transitions != expectedTransitions) return null;

            var finished = to is RequestState.Completed or RequestState.Cancelled or RequestState.Failed;

            var moved = request with
            {
                State = to,
                Reason = reason ?? request.Reason,
                EndedAt = finished ? DateTimeOffset.UtcNow : null,
                Transitions = request.Transitions + 1,
                Intent = intent ?? request.Intent,
                CommittedHash = committedHash ?? request.CommittedHash
            };

            _requests[requestId] = moved;
            return moved;
        }
    }

    public RequestTrace? Request(Ulid requestId)
    {
        lock (_gate) return _requests.GetValueOrDefault(requestId);
    }

    public IReadOnlyList<RequestTrace> Unfinished()
    {
        lock (_gate) return [.. _requests.Values.Where(static request => !request.IsFinished).OrderBy(static request => request.Id)];
    }

    public void Record(ExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate) _steps[step.Id] = step;
    }

    public int Interrupt(Ulid requestId)
    {
        lock (_gate)
        {
            var marked = 0;
            foreach (var step in _steps.Values.Where(step => step.RequestId == requestId && step.Status is StepStatus.Running).ToList())
            {
                _steps[step.Id] = step with { Status = StepStatus.Interrupted };
                marked++;
            }

            return marked;
        }
    }

    public void Record(DataChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate) _changes.Add(change);
    }

    /// <summary>Removes the changes a rolled-back unit of work recorded: the in-memory storage tells it which.</summary>
    public void Forget(IEnumerable<Ulid> changeIds)
    {
        var ids = changeIds.ToHashSet();
        lock (_gate) _changes.RemoveAll(change => ids.Contains(change.Id));
    }

    public (RequestTrace Request, IReadOnlyList<ExecutionStep> Steps)? Read(Ulid requestId)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var request)) return null;

            return (request, [.. _steps.Values.Where(step => step.RequestId == requestId)
                .OrderBy(static step => step.StartedAt).ThenBy(static step => step.Id)]);
        }
    }

    public IReadOnlyList<DataChange> Changes(Ulid requestId)
    {
        lock (_gate) return [.. _changes.Where(change => change.RequestId == requestId).OrderBy(static change => change.Id)];
    }

    public IReadOnlyList<DataChange> ChangesBefore(DateTimeOffset moment)
    {
        lock (_gate) return [.. _changes.Where(change => change.At < moment).OrderBy(static change => change.Id)];
    }

    public int ClearPayloadsNaming(Ulid recordId)
    {
        lock (_gate)
        {
            var requests = _changes.Where(change => change.RecordId == recordId).Select(static change => change.RequestId).ToHashSet();
            var cleared = 0;
            foreach (var step in _steps.Values.Where(step => requests.Contains(step.RequestId) && (step.Input is not null || step.Output is not null)).ToList())
            {
                _steps[step.Id] = step with { Input = null, Output = null };
                cleared++;
            }

            return cleared;
        }
    }

    public int PurgeDiagnostics(DateTimeOffset moment)
    {
        lock (_gate)
        {
            var purged = _requests.Values
                .Where(request => request.IsFinished && request.EndedAt is { } ended && ended < moment)
                .Select(static request => request.Id)
                .ToList();

            foreach (var id in purged)
            {
                _requests.Remove(id);
                foreach (var step in _steps.Values.Where(step => step.RequestId == id).ToList()) _steps.Remove(step.Id);
            }

            return purged.Count;
        }
    }
}
