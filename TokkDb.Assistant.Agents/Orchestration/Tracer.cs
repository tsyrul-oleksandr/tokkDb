using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Records the steps of one request as they happen (TR-1, TR-2a, TR-4a): each step running when
/// it starts, completed or failed when it ends, and every step naming the one before it, which is
/// what the diagram is drawn from. Steps a model call makes are the runner's; this adopts them so
/// the chain stays whole.
/// </summary>
public sealed class Tracer
{
    private readonly ITraceRecorder _recorder;

    public Tracer(ITraceRecorder recorder, RequestTrace request)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public RequestTrace Request { get; }

    /// <summary>The step the next one follows.</summary>
    public Ulid? Last { get; private set; }

    private readonly Lock _clock = new();
    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;

    /// <summary>
    /// The moment a step starts, strictly later than the step before it: two notes made in the
    /// same tick would otherwise read back in either order, since the stores order steps by
    /// when they started and the ids issued within a millisecond do not order.
    /// </summary>
    private DateTimeOffset Now()
    {
        lock (_clock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now <= _lastStarted) now = _lastStarted.AddTicks(1);
            _lastStarted = now;
            return now;
        }
    }

    /// <summary>Begins a step: it is on disk as running before the work starts (TR-4a, TR-4b).</summary>
    public StepScope Step(string name, string? input = null)
    {
        var step = new ExecutionStep(Ulid.NewUlid(), Request.Id, name, StepStatus.Running, Now())
        {
            After = Last,
            Input = input
        };

        _recorder.Record(step);
        Last = step.Id;

        return new StepScope(_recorder, step);
    }

    /// <summary>A step that started and ended at once: what the person said, what was decided without a call.</summary>
    public ExecutionStep Note(string name, string? input = null, string? output = null)
    {
        var now = Now();
        var step = new ExecutionStep(Ulid.NewUlid(), Request.Id, name, StepStatus.Completed, now, now)
        {
            After = Last,
            Input = input,
            Output = output
        };

        _recorder.Record(step);
        Last = step.Id;

        return step;
    }

    /// <summary>A step the runner recorded, taken as the last one so the chain continues from it.</summary>
    public void Adopt(ExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Last = step.Id;
    }

    /// <summary>One step, open while its work runs. Ends as completed with an output, or failed with a reason.</summary>
    public sealed class StepScope
    {
        private readonly ITraceRecorder _recorder;
        private ExecutionStep _step;
        private bool _ended;

        internal StepScope(ITraceRecorder recorder, ExecutionStep step)
        {
            _recorder = recorder;
            _step = step;
        }

        public Ulid Id => _step.Id;

        public ExecutionStep Done(string? output = null)
        {
            if (_ended) return _step;
            _ended = true;
            _step = _step with { Status = StepStatus.Completed, EndedAt = DateTimeOffset.UtcNow, Output = output };
            _recorder.Record(_step);
            return _step;
        }

        public ExecutionStep Failed(string reason)
        {
            if (_ended) return _step;
            _ended = true;
            _step = _step with { Status = StepStatus.Failed, EndedAt = DateTimeOffset.UtcNow, Output = reason };
            _recorder.Record(_step);
            return _step;
        }
    }
}
