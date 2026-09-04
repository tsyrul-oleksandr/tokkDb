namespace TokkDb.LLM.Core.Diagnostics;

public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly List<DiagnosticEvent> _events = new();
    private readonly object _sync = new();

    public event EventHandler<DiagnosticEvent>? EventAdded;

    public IReadOnlyCollection<DiagnosticEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    public void Log(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        lock (_sync)
        {
            _events.Add(diagnosticEvent);
        }

        EventAdded?.Invoke(this, diagnosticEvent);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _events.Clear();
        }
    }
}
