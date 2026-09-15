namespace TokkDb.Assistant.Agents.Models;

/// <summary>
/// How a model call went, by mode (AG-1e). Counted per mode by the harness, because a repaired
/// malformed output and an empty reply under context pressure are different events: the first is
/// a cost, the second is the failure that actually occurred in the spike.
/// </summary>
public enum ModelOutcome
{
    /// <summary>A usable answer, first time.</summary>
    Valid = 1,

    /// <summary>A usable answer, after at least one malformed one was sent back for repair (AG-5).</summary>
    MalformedRepaired,

    /// <summary>Malformed past the repair bound; the operation failed with a stated reason.</summary>
    MalformedPastBound,

    /// <summary>Nothing came back: the model produced no text, which is what a filled window does.</summary>
    EmptyReply,

    /// <summary>The call did not finish in time.</summary>
    Timeout,

    /// <summary>The model declined to answer.</summary>
    Refusal,

    /// <summary>
    /// The answer ran past the output cap and was cut off, so it could not be whole (R-2b). Not
    /// sent back for repair: the same prompt would be cut at the same place.
    /// </summary>
    CutOff
}

/// <summary>
/// What one call to the model returned, with the figures §6.2a measures: tokens both ways,
/// round trips counted apart from calls, and the most of the window in use at any point.
/// </summary>
public sealed record ModelReply(
    string Text,
    int PromptTokens,
    int CompletionTokens,
    int RoundTrips,
    int PeakContextTokens,
    TimeSpan Elapsed,
    string? FinishReason)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>The transport stopped the answer at the output cap: what came back is a prefix, not an answer.</summary>
    public bool WasCutOff => string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An operation could not get a usable answer (AG-5): malformed past the bound, empty, timed out
/// or refused. The reason is one a person can act on, and the mode is what the harness counts.
/// </summary>
public sealed class ModelFailedException : Exception
{
    public ModelFailedException(string operation, ModelOutcome outcome, string reason, Exception? inner = null)
        : base($"{operation}: {reason}", inner)
    {
        Operation = operation;
        Outcome = outcome;
        Reason = reason;
    }

    public string Operation { get; }
    public ModelOutcome Outcome { get; }
    public string Reason { get; }
}
