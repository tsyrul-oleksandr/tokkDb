using System.Text.Json;
using TokkDb.Assistant.Agents.Models;

namespace TokkDb.Assistant.App.Settings;

/// <summary>
/// What the application remembers between runs, as one small document in the platform's
/// application data location (NF-4e): where the model is, whether anything may leave the machine
/// (NF-4), and which model. Never a credential: those live in the platform's secret store (NF-4c).
/// </summary>
public sealed record AppSettings
{
    public string Endpoint { get; init; } = ModelEndpoint.Ollama.Address.ToString();

    public EgressMode Egress { get; init; } = EgressMode.LocalOnly;

    public string Model { get; init; } = ModelConfiguration.Default.Model;

    public int ContextSize { get; init; } = ModelConfiguration.Default.ContextSize;

    public bool Think { get; init; } = ModelConfiguration.Default.Think;

    /// <summary>Step 9.2's three windows, in days: diagnostics, the change journal, and how long a change can be taken back (which is also how long history is kept).</summary>
    public int DiagnosticsDays { get; init; } = (int)Trace.RetentionWindows.Default.Diagnostics.TotalDays;

    public int ChangesDays { get; init; } = (int)Trace.RetentionWindows.Default.Changes.TotalDays;

    public int CompensationDays { get; init; } = (int)Trace.RetentionWindows.Default.Compensation.TotalDays;

    public Trace.RetentionWindows Retention => new(
        TimeSpan.FromDays(Math.Max(1, DiagnosticsDays)),
        TimeSpan.FromDays(Math.Max(1, ChangesDays)),
        TimeSpan.FromDays(Math.Max(1, CompensationDays)),
        TimeSpan.FromDays(Math.Max(1, CompensationDays)));

    /// <summary>The database, private to the user in the application's data directory (NF-4e).</summary>
    public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "storage.db");

    public static string SettingsPath => Path.Combine(FileSystem.AppDataDirectory, "settings.json");

    public ModelEndpoint ModelEndpoint => new(new Uri(Endpoint));

    public ModelConfiguration Configuration => new("ollama", Model, ContextSize, ModelConfiguration.Default.Temperature, Think);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch (Exception failure) when (failure is IOException or JsonException)
        {
            // Unreadable settings are the defaults, not a crash before the window opens.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
