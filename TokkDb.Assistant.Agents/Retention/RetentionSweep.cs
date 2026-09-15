using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Retention;

/// <summary>What one sweep did, for the log and for a test.</summary>
public sealed record SweepReport(
    DateTimeOffset DiagnosticsBefore,
    DateTimeOffset HistoryBefore,
    int RequestsPurged,
    int RecordsSwept,
    int VersionsPurged);

/// <summary>
/// The two purges of step 9.2 (TR-7, TR-8, NF-4d), run together under one set of windows: the
/// diagnostics of requests that finished before the diagnostics window, and the version history
/// older than the history window - which is never inside the compensation window, because
/// <see cref="RetentionWindows.PurgeHistoryBefore"/> refuses such a configuration naming both
/// moments before anything is touched. Data, conversations and the change journal are on no
/// window here; the journal is what the history purge walks, so every record a change ever named
/// is swept, whether or not it still exists.
/// </summary>
public static class RetentionSweep
{
    public static SweepReport Run(IStorage storage, ITraceRecorder recorder, RetentionWindows windows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(windows);

        // Checked first, so that a refused configuration purges nothing at all (AJ-8).
        var historyBefore = windows.PurgeHistoryBefore(now);
        var diagnosticsBefore = windows.PurgeDiagnosticsBefore(now);

        var requests = recorder.PurgeDiagnostics(diagnosticsBefore);

        var records = recorder.ChangesBefore(now)
            .Where(static change => change.RecordId is not null)
            .Select(static change => (change.CollectionName, Id: change.RecordId!.Value))
            .Distinct()
            .ToList();

        var swept = 0;
        var versions = 0;
        foreach (var (collection, id) in records)
        {
            if (storage.GetCollectionDefinition(collection) is null) continue;
            versions += storage.PurgeRecordHistory(collection, id, historyBefore);
            swept++;
        }

        return new SweepReport(diagnosticsBefore, historyBefore, requests, swept, versions);
    }

    /// <summary>
    /// NF-4d: ending the compensation window early for one request - the history of every record
    /// it changed is purged up to now, so nothing it removed or overwrote can be put back, and a
    /// value that existed only in those versions is gone from the file (NF-4d1). The step payloads
    /// of the requests that named those records go with it, because "what it would do" and "the
    /// change" quoted the values that are being forgotten.
    /// </summary>
    /// <returns>How many versions went.</returns>
    public static int EndWindow(IStorage storage, ITraceRecorder recorder, Ulid requestId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(recorder);

        var versions = 0;
        foreach (var change in recorder.Changes(requestId))
        {
            if (change.RecordId is not { } id || storage.GetCollectionDefinition(change.CollectionName) is null) continue;

            // Payloads first, history second: the purge is the transaction whose commit leaves no
            // frame of what went before it in the journal (V-15), the clearing on its own is not.
            recorder.ClearPayloadsNaming(id);
            versions += storage.PurgeRecordHistory(change.CollectionName, id, now);
        }

        return versions;
    }
}
