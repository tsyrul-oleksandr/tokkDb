namespace TokkDb.LLM.Core.Diagnostics;

public interface IDiagnosticsService
{
    event EventHandler<DiagnosticEvent>? EventAdded;

    IReadOnlyCollection<DiagnosticEvent> Events { get; }

    void Log(DiagnosticEvent diagnosticEvent);

    void Clear();
}
