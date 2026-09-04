namespace TokkDb.LLM.Core;

/// <summary>
/// Tokens a turn consumed, as reported by the provider.
///
/// A turn that calls tools makes several round trips to the model, and each one
/// reports its own usage; these are the totals for the whole turn, which is what
/// the turn actually cost.
///
/// Provider-independent: every client normalises its own counters into this
/// shape, so nothing here is specific to Ollama or OpenAI.
/// </summary>
public sealed record AgentTokenUsage(long InputTokens, long OutputTokens, long TotalTokens)
{
    public static readonly AgentTokenUsage None = new(0, 0, 0);

    public bool HasValue => InputTokens > 0 || OutputTokens > 0 || TotalTokens > 0;

    /// <summary>Adds another round trip's usage to this one.</summary>
    public AgentTokenUsage Add(long input, long output, long total) =>
        new(InputTokens + input,
            OutputTokens + output,
            // Providers that omit a total still give a usable figure this way.
            TotalTokens + (total > 0 ? total : input + output));
}
