namespace TokkDb.Assistant.Trace;

/// <summary>
/// Where the durability point of the diagnostics is (TR-4c), stated rather than assumed.
///
/// <b>Two rules, not one</b> (TR-4, TR-4a). The change record is inside the transaction of the
/// mutation and is atomic with it; a lifecycle transition is durable at every transition. Those
/// two are not configurable. The diagnostic steps are the third thing: persisting each one on its
/// own is a commit per step, and the journal makes a commit real work, so a recorder may hold
/// them for a bounded delay and write them together - at the latest when the delay has passed,
/// when the next transition or change is recorded, when the trace is next read, or when the
/// recorder is disposed. With a delay, the last step before a crash may be lost; a transition
/// never is, and startup reconciliation marks whatever was left running as interrupted (TR-4b).
/// </summary>
/// <param name="DiagnosticDelay">
/// How long a diagnostic step may wait before it is on disk. <see cref="TimeSpan.Zero"/> means
/// each step is written as it is recorded.
/// </param>
public sealed record TraceDurability(TimeSpan DiagnosticDelay)
{
    /// <summary>
    /// Every step written as it happens. The default because the bound is then zero and nothing
    /// is lost; a recorder under load may choose a delay, and says so here rather than in a
    /// comment.
    /// </summary>
    public static readonly TraceDurability Immediate = new(TimeSpan.Zero);

    /// <summary>The default: <see cref="Immediate"/>.</summary>
    public static readonly TraceDurability Default = Immediate;

    public bool IsImmediate => DiagnosticDelay <= TimeSpan.Zero;
}
