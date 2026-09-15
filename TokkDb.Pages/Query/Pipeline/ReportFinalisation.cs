using System.Diagnostics;

namespace TokkDb.Pages.Query.Pipeline;

//DG-4, stage six: closes the figures after the enumeration ends. Owns elapsed.
//
//A report is closed once, whether the run completed, failed or was cancelled (DG-3a), and the
//figures it carries are final from then on (DG-5).
internal sealed class ReportFinalisation {
  private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
  private TimeSpan? _elapsed;

  public TimeSpan Elapsed => _elapsed ?? _stopwatch.Elapsed;

  public TimeSpan Close() {
    if (_elapsed is null) {
      _stopwatch.Stop();
      _elapsed = _stopwatch.Elapsed;
    }
    return _elapsed.Value;
  }
}
