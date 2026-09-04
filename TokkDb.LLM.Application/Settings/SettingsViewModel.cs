using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Application.Settings;

public sealed class SettingsViewModel : BindableObject
{
    /// <summary>
    /// Sentinel shown in the per-operation provider picker meaning
    /// "inherit the default provider configuration".
    /// </summary>
    public const string UseDefaultOption = "Use default";

    public SettingsViewModel()
    {
        Providers = new ObservableCollection<string>(
            Enum.GetNames<LlmProviderKind>());

        Themes = new ObservableCollection<string>
        {
            "System",
            "Light",
            "Dark"
        };

        StorageBackends = new ObservableCollection<string>();

        SelectGeneralCommand =
            new Command(() => SelectedSection = SettingsSection.General);

        SelectAiCommand =
            new Command(() => SelectedSection = SettingsSection.Ai);

        SelectStorageCommand =
            new Command(() => SelectedSection = SettingsSection.Storage);

        SelectAdvancedCommand =
            new Command(() => SelectedSection = SettingsSection.Advanced);

        SaveGeneralCommand =
            new Command(SaveGeneralSettings);

        SaveAiSettingsCommand =
            new Command(SaveAiSettings);

        ResetAiSettingsCommand =
            new Command(ResetAiSettings);

        TestConnectionCommand =
            new Command(TestConnection);

        LoadModelsCommand =
            new Command(LoadModels);

        OperationTypes = new ObservableCollection<string>(
            Enum.GetNames<AgentOperationType>());

        OverrideProviders = new ObservableCollection<string>(
            new[] { UseDefaultOption }.Concat(Enum.GetNames<LlmProviderKind>()));

        SaveOperationOverrideCommand =
            new Command(SaveOperationOverride);

        ClearOperationOverrideCommand =
            new Command(ClearOperationOverride);

        SaveStorageCommand =
            new Command(SaveStorageSettings);

        BrowseStorageCommand =
            new Command(BrowseStorage);

        ResetAllSettingsCommand =
            new Command(ResetAllSettings);
    }


    // =========================================================
    // COLLECTIONS
    // =========================================================

    public ObservableCollection<string> Providers { get; }

    public ObservableCollection<string> Themes { get; }

    public ObservableCollection<string> StorageBackends { get; }

    public ObservableCollection<string> OperationTypes { get; }

    public ObservableCollection<string> OverrideProviders { get; }


    // =========================================================
    // NAVIGATION
    // =========================================================

    public ICommand SelectGeneralCommand { get; }

    public ICommand SelectAiCommand { get; }

    public ICommand SelectStorageCommand { get; }

    public ICommand SelectAdvancedCommand { get; }


    // =========================================================
    // COMMANDS
    // =========================================================

    public ICommand SaveGeneralCommand { get; }

    public ICommand SaveAiSettingsCommand { get; }

    public ICommand ResetAiSettingsCommand { get; }

    public ICommand TestConnectionCommand { get; }

    public ICommand LoadModelsCommand { get; }

    public ICommand SaveOperationOverrideCommand { get; }

    public ICommand ClearOperationOverrideCommand { get; }

    public ICommand SaveStorageCommand { get; }

    public ICommand BrowseStorageCommand { get; }

    public ICommand ResetAllSettingsCommand { get; }


    // =========================================================
    // SECTION
    // =========================================================

    public SettingsSection SelectedSection
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGeneralSelected));
            OnPropertyChanged(nameof(IsAiSelected));
            OnPropertyChanged(nameof(IsStorageSelected));
            OnPropertyChanged(nameof(IsAdvancedSelected));
        }
    } = SettingsSection.General;

    public bool IsGeneralSelected =>
        SelectedSection == SettingsSection.General;

    public bool IsAiSelected =>
        SelectedSection == SettingsSection.Ai;

    public bool IsStorageSelected =>
        SelectedSection == SettingsSection.Storage;

    public bool IsAdvancedSelected =>
        SelectedSection == SettingsSection.Advanced;


    // =========================================================
    // GENERAL
    // =========================================================

    public string ApplicationName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "TokkDb";

    public string SelectedTheme
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "System";

    // =========================================================
    // AI & LLM
    // =========================================================

    public string SelectedProvider
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = Settings.Instance.Provider.ToString();

    public string ProviderUrl
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = Settings.Instance.ProviderUrl;

    public string? AuthenticationToken
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string Model
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = Settings.Instance.ProviderModel;

    /// <summary>
    /// Context window in tokens, edited as text so an empty box can mean
    /// "use the default" rather than zero.
    /// </summary>
    public string ContextSize
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = Settings.Instance.ContextSize.ToString(CultureInfo.InvariantCulture);

    public string ConnectionStatus
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "Not tested";

    // =========================================================
    // PER-OPERATION PROVIDER OVERRIDES
    // =========================================================

    public string SelectedOperationType
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            OnPropertyChanged();
            LoadOperationOverride();
        }
    } = nameof(AgentOperationType.Chat);

    public string OverrideProvider
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = UseDefaultOption;

    public string? OverrideUrl
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string? OverrideModel
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string? OverrideAuthenticationToken
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string? OverrideContextSize
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string OperationOverrideStatus
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "Using default provider settings.";

    // =========================================================
    // STORAGE
    // =========================================================

    public string SelectedStorageBackend
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "Memory";

    public string? StoragePath
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    // =========================================================
    // ADVANCED
    // =========================================================

    public bool EnableDiagnostics
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public bool EnableToolExecutionLogs
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    // =========================================================
    // INFO
    // =========================================================

    public string ApplicationVersion =>
        $"Version {AppInfo.VersionString}";


    // =========================================================
    // METHODS
    // =========================================================

    private void SaveGeneralSettings()
    {
        if (Enum.TryParse<LlmProviderKind>(
                SelectedProvider,
                true,
                out var provider))
        {
            Settings.Instance.Provider = provider;
        }
        Settings.Instance.ProviderModel = Model;
        Settings.Instance.ProviderUrl = ProviderUrl;
        //todo
    }


    private void SaveAiSettings()
    {
        if (Enum.TryParse<LlmProviderKind>(SelectedProvider, true, out var provider))
        {
            Settings.Instance.Provider = provider;
        }

        Settings.Instance.ProviderUrl = ProviderUrl;
        Settings.Instance.ProviderModel = Model;
        Settings.Instance.AuthenticationToken =
            string.IsNullOrWhiteSpace(AuthenticationToken) ? null : AuthenticationToken.Trim();

        var contextSize = Settings.NormalizeContextSize(ParseContextSize(ContextSize));
        Settings.Instance.ContextSize = contextSize;
        // Show the value that was actually stored, so a clamped or rejected
        // entry does not look accepted.
        ContextSize = contextSize.ToString(CultureInfo.InvariantCulture);

        ConnectionStatus = "Settings saved";
    }


    private void LoadOperationOverride()
    {
        if (!Enum.TryParse<AgentOperationType>(SelectedOperationType, true, out var operationType))
        {
            return;
        }

        var existing = Settings.Instance.GetOperationOverride(operationType);
        OverrideProvider = existing?.Provider?.ToString() ?? UseDefaultOption;
        OverrideUrl = existing?.Url;
        OverrideModel = existing?.Model;
        OverrideAuthenticationToken = existing?.AuthenticationToken;
        OverrideContextSize = existing?.ContextSize?.ToString(CultureInfo.InvariantCulture);
        OperationOverrideStatus = existing is null
            ? $"{SelectedOperationType} uses the default provider settings."
            : $"{SelectedOperationType} has a custom provider configuration.";
    }


    private void SaveOperationOverride()
    {
        if (!Enum.TryParse<AgentOperationType>(SelectedOperationType, true, out var operationType))
        {
            OperationOverrideStatus = "Select an operation type first.";
            return;
        }

        var settings = new OperationProviderSettings
        {
            Provider = Enum.TryParse<LlmProviderKind>(OverrideProvider, true, out var provider)
                ? provider
                : null,
            Url = string.IsNullOrWhiteSpace(OverrideUrl) ? null : OverrideUrl.Trim(),
            Model = string.IsNullOrWhiteSpace(OverrideModel) ? null : OverrideModel.Trim(),
            AuthenticationToken = string.IsNullOrWhiteSpace(OverrideAuthenticationToken)
                ? null
                : OverrideAuthenticationToken.Trim(),
            ContextSize = ParseContextSize(OverrideContextSize) is { } size
                ? Settings.NormalizeContextSize(size)
                : null
        };

        OverrideContextSize = settings.ContextSize?.ToString(CultureInfo.InvariantCulture);

        Settings.Instance.SetOperationOverride(operationType, settings);
        OperationOverrideStatus = settings.IsEmpty
            ? $"{SelectedOperationType} uses the default provider settings."
            : $"Saved provider configuration for {SelectedOperationType}.";
    }


    private void ClearOperationOverride()
    {
        if (!Enum.TryParse<AgentOperationType>(SelectedOperationType, true, out var operationType))
        {
            return;
        }

        Settings.Instance.SetOperationOverride(operationType, null);
        OverrideProvider = UseDefaultOption;
        OverrideUrl = null;
        OverrideModel = null;
        OverrideAuthenticationToken = null;
        OverrideContextSize = null;
        OperationOverrideStatus = $"{SelectedOperationType} reset to the default provider settings.";
    }


    /// <summary>
    /// Reads a context size from user text. Blank or unparseable input returns
    /// null, which means "use the default" rather than an error.
    /// </summary>
    private static int? ParseContextSize(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;


    private void ResetAiSettings()
    {
        SelectedProvider = LlmProviderKind.Ollama.ToString();
        ProviderUrl = "http://192.168.0.5:11434";
        AuthenticationToken = null;
        Model = "qwen3.5:9b";
        ContextSize = Settings.DefaultContextSize.ToString(CultureInfo.InvariantCulture);

        ConnectionStatus = "Default settings restored";
    }


    private void TestConnection()
    {
        ConnectionStatus = "Testing connection...";

        // TODO:
        // Use ILlmProviderFactory
        // and execute provider health/model request.
    }


    private void LoadModels()
    {
        ConnectionStatus = "Loading available models...";

        // TODO:
        // Load models from Ollama/OpenAI provider.
    }


    private void SaveStorageSettings()
    {
        // TODO:
        // Persist storage configuration.
    }


    private void BrowseStorage()
    {
        // Platform-specific implementation.
        ConnectionStatus =
            "Storage folder selection is not implemented yet.";
    }


    private void ResetAllSettings()
    {
        ApplicationName = "TokkDb";
        SelectedTheme = "System";

        ResetAiSettings();

        Settings.Instance.ClearOperationOverrides();
        OverrideProvider = UseDefaultOption;
        OverrideUrl = null;
        OverrideModel = null;
        OverrideAuthenticationToken = null;
        OperationOverrideStatus = "All per-operation overrides removed.";

        EnableDiagnostics = false;
        EnableToolExecutionLogs = true;

        ConnectionStatus = "All settings restored to defaults.";
    }
}
