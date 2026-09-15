namespace TokkDb.Assistant.Agents.Models;

/// <summary>
/// Which model an operation talks to, and how (AG-7): provider, model, context size and
/// temperature, with a default and per-operation overrides in <see cref="ModelSettings"/>.
///
/// The context size is the capability limit of the call - what the transport asks Ollama to load
/// the model with - and never the target: the target is the operation's context budget (AG-1a),
/// which is far below it. The two are different numbers on purpose.
/// </summary>
/// <param name="Provider">Who serves the model: <c>ollama</c> today.</param>
/// <param name="Model">The model's name as the provider knows it.</param>
/// <param name="ContextSize">How many tokens of window the model is loaded with (<c>num_ctx</c>).</param>
/// <param name="Temperature">How much the answer may vary. Low, because these are structured answers.</param>
/// <param name="Think">
/// Whether the model may reason before answering. Off by default: step 0.3 measured that on
/// this model reasoning runs until something stops it, and what stopped it emptied the reply.
/// </param>
public sealed record ModelConfiguration(
    string Provider,
    string Model,
    int ContextSize,
    float Temperature,
    bool Think = false)
{
    /// <summary>D-3's default: the local model, a sixteen-thousand-token window, a low temperature.</summary>
    public static readonly ModelConfiguration Default = new("ollama", "qwen3.5:4b", 16_384, 0.2f);
}

/// <summary>
/// The default model configuration and the per-operation overrides (AG-7): extraction and
/// mapping can be pointed at different models by changing this, not the code.
/// </summary>
public sealed class ModelSettings
{
    private readonly Dictionary<string, ModelConfiguration> _overrides = new(StringComparer.Ordinal);

    public ModelSettings(ModelConfiguration? defaults = null)
    {
        Default = defaults ?? ModelConfiguration.Default;
    }

    public ModelConfiguration Default { get; }

    public IReadOnlyDictionary<string, ModelConfiguration> Overrides => _overrides;

    /// <summary>Points one operation, by name, at a configuration of its own.</summary>
    public ModelSettings Override(string operationName, ModelConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(configuration);
        _overrides[operationName] = configuration;
        return this;
    }

    /// <summary>The configuration an operation runs with: its override, else the default.</summary>
    public ModelConfiguration For(string operationName) =>
        _overrides.TryGetValue(operationName, out var configured) ? configured : Default;
}

/// <summary>
/// Whether anything may leave the machine (NF-4). A mode, not a preference: <see cref="LocalOnly"/>
/// refuses a remote endpoint rather than warning about one, and <see cref="RemoteAllowed"/> is an
/// explicit choice that the interface marks permanently.
/// </summary>
public enum EgressMode
{
    /// <summary>The default. Loopback only; a LAN address is remote (NF-4c).</summary>
    LocalOnly = 1,

    /// <summary>A remote endpoint is allowed, and the interface says so on every screen.</summary>
    RemoteAllowed
}

/// <summary>The egress mode as a registered setting: what the interface reads to show its indicator (NF-4).</summary>
public sealed record EgressSettings(EgressMode Mode)
{
    public bool IsRemoteAllowed => Mode is EgressMode.RemoteAllowed;
}

/// <summary>
/// What an operation sends, declared beside its model configuration and context budget (NF-4a),
/// from least to most. An operation may send less than it declares and never more; the runner
/// refuses a context whose class is above the operation's.
/// </summary>
public enum EgressClass
{
    /// <summary>Nothing of the user's: instructions and a question about a shape, at most.</summary>
    Nothing = 1,

    /// <summary>The schema digest - names, purposes and kinds - and no values.</summary>
    SchemaDigest,

    /// <summary>The digest plus a bounded sample of values: a profile, a few example cells.</summary>
    BoundedSample,

    /// <summary>Raw text the user brought, in chunks: what extraction has to see.</summary>
    RawText
}

/// <summary>Where the model is served from, and whether that is local (NF-4c).</summary>
public sealed record ModelEndpoint(Uri Address)
{
    public static readonly ModelEndpoint Ollama = new(new Uri("http://localhost:11434"));

    /// <summary>
    /// "Local" defined rather than assumed (NF-4c): loopback only. A name that resolves to a LAN
    /// address, or an address on the LAN, is remote.
    /// </summary>
    public bool IsLocal =>
        Address.IsLoopback
        || string.Equals(Address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || Address.Host is "127.0.0.1" or "::1" or "[::1]";
}

/// <summary>
/// NF-4's refusal: a remote endpoint in <see cref="EgressMode.LocalOnly"/>, named with the mode
/// so that the person knows what to change and that it is a choice.
/// </summary>
public sealed class EgressRefusedException : Exception
{
    public EgressRefusedException(ModelEndpoint endpoint, EgressMode mode)
        : base($"The model endpoint {endpoint.Address} is not on this machine, and the egress mode is {mode}: " +
               "nothing may leave the machine. Choose a local endpoint, or choose RemoteAllowed deliberately.")
    {
        Endpoint = endpoint;
        Mode = mode;
    }

    public ModelEndpoint Endpoint { get; }
    public EgressMode Mode { get; }
}

/// <summary>The one rule of NF-4, applied where the endpoint is configured.</summary>
public static class Egress
{
    /// <exception cref="EgressRefusedException">The endpoint is remote and the mode is <see cref="EgressMode.LocalOnly"/>.</exception>
    public static ModelEndpoint Check(ModelEndpoint endpoint, EgressMode mode)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (mode is EgressMode.LocalOnly && !endpoint.IsLocal)
        {
            throw new EgressRefusedException(endpoint, mode);
        }

        return endpoint;
    }
}
