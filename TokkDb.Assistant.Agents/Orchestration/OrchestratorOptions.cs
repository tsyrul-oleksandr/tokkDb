using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>What the orchestrator can be told, with the defaults the plan gives.</summary>
public sealed record OrchestratorOptions
{
    /// <summary>How many records a reply shows at once (QR-3a bounds the handle by this).</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>The floor and the gap of AG-3b.</summary>
    public PlacementThresholds Thresholds { get; init; } = PlacementThresholds.Default;

    /// <summary>What an import does with a row that is already there (IN-7): skip, unless told otherwise.</summary>
    public MergePolicy MergePolicy { get; init; } = MergePolicy.SkipExisting;

    /// <summary>The three windows of step 9.2; the compensation window is what a card states (NF-4d).</summary>
    public RetentionWindows Retention { get; init; } = RetentionWindows.Default;

    /// <summary>
    /// How many prompt tokens the schema digest may take in a prefix (§6.1). One thousand: step
    /// 0.4 measured the model reading a digest of that size seven times in ten and a digest of
    /// four thousand a third of the time, so past this the digest is a count with names and the
    /// shortlist is what the model chooses from.
    /// </summary>
    public int DigestBudget { get; init; } = 1_000;

    /// <summary>Today, for the query operation's "last year" and for the trace. Replaceable in tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;
}
