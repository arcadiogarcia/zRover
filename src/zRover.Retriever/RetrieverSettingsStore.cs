using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace zRover.Retriever;

/// <summary>
/// Persists user-configured settings across restarts using a JSON file in
/// the application's local data folder.
/// </summary>
public sealed class RetrieverSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetrieverSettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<RetrieverSettingsStore> _logger;

    public RetrieverSettingsStore(ILogger<RetrieverSettingsStore> logger)
    {
        _logger = logger;
    }

    public RetrieverSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<RetrieverSettings>(json);
                if (settings is not null)
                {
                    _logger.LogInformation("Loaded persisted settings from {Path}", SettingsPath);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path} — using defaults", SettingsPath);
        }

        return new RetrieverSettings();
    }

    public void Save(RetrieverSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }
}

public sealed class RetrieverSettings
{
    public bool ExternalEnabled { get; set; }
    public int ExternalPort { get; set; } = 5201;
    public string? ExternalBearerToken { get; set; }
    public bool PackageInstallEnabled { get; set; }
}
