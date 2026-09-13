namespace TokkDb.Assistant.Trace;

/// <summary>
/// How a step ended, or that it did not (TR-4b).
///
/// <b>A step interrupted by a crash must never read as completed.</b> That is the whole reason
/// the status is written down rather than inferred from whether an end time is present: a missing
/// end time and a step that never finished look identical, and a diagram that implied a result
/// which was never produced would be worse than one that said nothing.
/// </summary>
public enum StepStatus
{
    Running = 1,
    Completed,
    Failed,

    /// <summary>The process went while it was running. Startup reconciliation marks these.</summary>
    Interrupted
}

/// <summary>
/// One step of a request: what was done, when, how it went, and what went in and came out
/// (TR-2a).
///
/// The edges of the diagram are here rather than in a list of their own: a step names the step it
/// followed, so the trace is a graph without a second collection to keep in step with the first.
/// </summary>
public sealed record ExecutionStep(
    Ulid Id,
    Ulid RequestId,
    string Name,
    StepStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null)
{
    /// <summary>The step this one followed, or null for the first.</summary>
    public Ulid? After { get; init; }

    /// <summary>What the step was given, rendered for a person to read (TR-6).</summary>
    public string? Input { get; init; }

    /// <summary>What it produced.</summary>
    public string? Output { get; init; }

    /// <summary>The model call this step was, where it was one.</summary>
    public ModelCall? Call { get; init; }

    public TimeSpan? Took => EndedAt is { } ended ? ended - StartedAt : null;
}

/// <summary>
/// What one call to a model cost (TR-3, §6.2a).
///
/// <b>A hash of the prompt, never the prompt.</b> D-8 gives the reason: storing prompt text would
/// put the user's content in the database twice and make traces larger than the data they
/// describe. The hash still answers the question the text was wanted for - whether two calls sent
/// the same thing - and AG-6a turns that into an assertion, because a byte-identical prefix is
/// what Ollama's cache is keyed on and a reordering of two blocks is invisible in review.
///
/// <b>Round trips are counted apart from calls</b>, and that is not tidiness. The spike measured
/// one logical call that became twenty-five of them, and the framework's own response object sums
/// the turns, so the loop was invisible in the aggregate. A budget that cannot see it cannot
/// assert against it.
/// </summary>
public sealed record ModelCall(
    string Model,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration,
    string PromptHash)
{
    /// <summary>How many times the framework went to the model for this one logical call.</summary>
    public int RoundTrips { get; init; } = 1;

    /// <summary>How many times a malformed answer had to be sent back for repair (AG-5).</summary>
    public int Retries { get; init; }

    /// <summary>
    /// The most of the context window that was in use at any point of the call. A hard assert
    /// rather than a metric: this is the leading indicator of the failure that actually occurred
    /// in the spike, where a filled window emptied the reply.
    /// </summary>
    public int PeakContextTokens { get; init; }

    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// The only way a <see cref="ModelCall"/> should be built, and the reason it exists is mechanical
/// rather than tidy (TR-3, D-8).
///
/// <c>ModelCall</c> takes a hash, and a caller with the prompt in hand has to hash it. Every
/// caller that does that by itself is a place where the prompt can be passed instead, and the
/// rule "a hash of the prompt, never the prompt" is then a thing people remember rather than a
/// thing the code does. This takes the prompt and cannot keep it.
/// </summary>
public static class ModelCalls
{
    public static ModelCall For(
        string model,
        string prompt,
        int promptTokens,
        int completionTokens,
        TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(prompt);

        return new ModelCall(model, promptTokens, completionTokens, duration, Hashes.Of(prompt));
    }
}
