using TokkDb.LLM.Core.Diagnostics;
using TokkDb.Pages.Query;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// UI-4: the storage-level measurements of every query, put where the application can see
/// them. The engine publishes a <see cref="QueryReport"/> and knows nothing about
/// diagnostics; this turns one into a <see cref="DiagnosticEvent"/>, and it lives here
/// because this is the innermost project that can see both.
///
/// The requirement is not decoration. §2.3's claim about DC-5 — that an indexed query reads a
/// handful of pages where a scan reads all of them — is a claim about the page-read count,
/// and the experimental chapter has to be able to read it out of the running application
/// rather than reconstruct it from timings.
/// </summary>
public sealed class QueryDiagnosticsReporter : IDisposable
{
    private readonly QueryService _queries;
    private readonly IDiagnosticsService _diagnostics;

    public QueryDiagnosticsReporter(QueryService queries, IDiagnosticsService diagnostics)
    {
        _queries = queries;
        _diagnostics = diagnostics;
        _queries.QueryExecuted += OnQueryExecuted;
    }

    public void Dispose()
    {
        _queries.QueryExecuted -= OnQueryExecuted;
    }

    private void OnQueryExecuted(QueryReport report)
    {
        _diagnostics.Log(new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            // A scan is reported louder than a seek. It is the one outcome the reader can act
            // on — by adding an index — and at Information it would be indistinguishable from
            // the queries that are already doing the right thing.
            report.AccessPath.StartsWith("full scan", StringComparison.Ordinal)
                ? DiagnosticLevel.Warning
                : DiagnosticLevel.Information,
            "TokkDb",
            "Query",
            report.AccessPath,
            $"{report.PagesRead} page reads, {report.RecordsExamined} records examined, " +
            $"{report.RecordsMatched} matched, {report.Elapsed.TotalMilliseconds:F2} ms",
            report.ToString()));
    }
}
