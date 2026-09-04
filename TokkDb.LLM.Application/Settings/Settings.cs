using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Application.Settings;

public record Settings
{
    public const int DefaultContextSize = 16384;
    public const int MinimumContextSize = 512;
    public const int MaximumContextSize = 1_048_576;

    public static Settings Instance { get; } = new();

    private readonly Dictionary<AgentOperationType, OperationProviderSettings> _operationOverrides = new();

    public LlmProviderKind Provider { get; set; } = LlmProviderKind.Ollama;
    public string ProviderUrl { get; set; } = "http://localhost:11434";//"http://192.168.0.5:11434";
    public string ProviderModel { get; set; } = "qwen3.5:4b";
    public string? AuthenticationToken { get; set; }

    /// <summary>
    /// Context window in tokens requested from the model. Ollama defaults to
    /// 4096, which a large tool surface exhausts quickly, so the application
    /// asks for a larger window by default.
    /// </summary>
    public int ContextSize { get; set; } = DefaultContextSize;
    public StorageBackend StorageType { get; set; } = StorageBackend.Memory;
    public string StorageFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokkDb", "tokkdb.db");

    private Settings() { }

    /// <summary>
    /// Clamps a requested context size into the supported range, falling back to
    /// the default when the value is missing or unusable.
    /// </summary>
    public static int NormalizeContextSize(int? contextSize) =>
        contextSize is null or <= 0
            ? DefaultContextSize
            : Math.Clamp(contextSize.Value, MinimumContextSize, MaximumContextSize);

    /// <summary>
    /// Returns the override configured for an operation type, or <c>null</c>
    /// when the operation uses the default provider configuration.
    /// </summary>
    public OperationProviderSettings? GetOperationOverride(AgentOperationType operationType)
    {
        lock (_operationOverrides)
        {
            return _operationOverrides.TryGetValue(operationType, out var settings) && !settings.IsEmpty
                ? settings
                : null;
        }
    }

    public void SetOperationOverride(AgentOperationType operationType, OperationProviderSettings? settings)
    {
        lock (_operationOverrides)
        {
            if (settings is null || settings.IsEmpty)
            {
                _operationOverrides.Remove(operationType);
                return;
            }

            _operationOverrides[operationType] = settings;
        }
    }

    public void ClearOperationOverrides()
    {
        lock (_operationOverrides)
        {
            _operationOverrides.Clear();
        }
    }

    public IReadOnlyCollection<AgentOperationType> ConfiguredOperationOverrides
    {
        get
        {
            lock (_operationOverrides)
            {
                return _operationOverrides.Keys.ToArray();
            }
        }
    }
}
