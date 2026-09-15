using Microsoft.Extensions.AI;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.TokenBudget.Harness;

/// <summary>
/// The single-token probe (§6.2a's methodology constraint): Ollama's prompt_tokens is unreliable
/// after a generation that filled the context, so before every working call the same prompt is
/// sent once more with the output capped at one token, and that figure - not the working call's
/// - is what the harness reports beside the budget. Keyed by the same hash the trace records
/// for the call, so the two are matched without keeping any prompt text.
/// </summary>
public sealed class ProbingChatClient : DelegatingChatClient
{
    private readonly Dictionary<string, int> _probed = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ProbingChatClient(IChatClient inner) : base(inner)
    {
    }

    /// <summary>Prompt tokens by the trace's prompt hash, summed over the attempts a call made.</summary>
    public IReadOnlyDictionary<string, int> Probed
    {
        get { lock (_gate) return new Dictionary<string, int>(_probed, StringComparer.Ordinal); }
    }

    public int Probes { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var hash = HashOf(list);

        var probing = options?.Clone() ?? new ChatOptions();
        probing.MaxOutputTokens = 1;
        probing.AdditionalProperties ??= [];
        probing.AdditionalProperties["num_predict"] = 1;
        probing.AdditionalProperties["think"] = false;

        var probe = await base.GetResponseAsync(list, probing, cancellationToken).ConfigureAwait(false);
        var tokens = (int)(probe.Usage?.InputTokenCount ?? 0);
        lock (_gate)
        {
            Probes++;
            _probed[hash] = _probed.GetValueOrDefault(hash) + tokens;
        }

        return await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The runner's hash: the system prefix and the first user content, which is what every attempt of one call shares.</summary>
    public static string HashOf(IReadOnlyList<ChatMessage> messages)
    {
        var system = messages.FirstOrDefault(static message => message.Role == ChatRole.System)?.Text ?? "";
        var user = messages.FirstOrDefault(static message => message.Role == ChatRole.User)?.Text ?? "";
        return Hashes.Of(system + "\n" + user);
    }
}
