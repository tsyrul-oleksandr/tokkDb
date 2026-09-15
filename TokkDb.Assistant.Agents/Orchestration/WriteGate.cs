namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// The single writer in the process (AG-10): writes serialise, reads never wait, and while a
/// write is under way the interface can say so. A delete from the browser during a long import
/// waits here and then succeeds; the browser reads throughout, because reading takes nothing.
/// </summary>
public sealed class WriteGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _holder;

    /// <summary>What is being written right now, or null when nothing is: the interface's "busy".</summary>
    public string? Busy => _holder;

    public bool IsBusy => _holder is not null;

    /// <summary>Waits for the writer's turn. Dispose the lease when the write has committed or rolled back.</summary>
    public async Task<Lease> EnterAsync(string what, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        _holder = what;
        return new Lease(this);
    }

    /// <summary>The writer's turn, taken now or not at all: for a caller that would rather report busy than wait.</summary>
    public bool TryEnter(string what, out Lease? lease)
    {
        if (_gate.Wait(0))
        {
            _holder = what;
            lease = new Lease(this);
            return true;
        }

        lease = null;
        return false;
    }

    public sealed class Lease : IDisposable
    {
        private WriteGate? _gate;

        internal Lease(WriteGate gate) => _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is null) return;
            gate._holder = null;
            gate._gate.Release();
        }
    }
}
