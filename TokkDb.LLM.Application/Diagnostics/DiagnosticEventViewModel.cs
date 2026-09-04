using TokkDb.LLM.Core.Diagnostics;

namespace TokkDb.LLM.Application.Diagnostics;

public sealed class DiagnosticEventViewModel
{
    public DiagnosticEventViewModel(DiagnosticEvent diagnosticEvent)
    {
        Event = diagnosticEvent;
    }

    public DiagnosticEvent Event { get; }

    public string Title => Event.Title;

    public string Summary => Event.Summary;

    public string Source => Event.Source;

    public string Category => Event.Category;

    public string Level => Event.Level.ToString();

    public string? Details => Event.Details;

    public string? Exception => Event.Exception;

    public bool HasException =>
        !string.IsNullOrWhiteSpace(Exception);

    public string TimeText =>
        Event.TimestampUtc.ToLocalTime()
            .ToString("HH:mm:ss");

    public string TimestampText =>
        Event.TimestampUtc.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss");

    public string Icon =>
        Event.Level switch
        {
            DiagnosticLevel.Trace => "•",
            DiagnosticLevel.Information => "ℹ",
            DiagnosticLevel.Warning => "⚠",
            DiagnosticLevel.Error => "✕",
            _ => "•"
        };
}
