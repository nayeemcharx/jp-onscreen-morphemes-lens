using System.IO;
using System.Text.Json;
using JPLens.Models;
using Microsoft.Extensions.Logging;

namespace JPLens.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the user's
/// application data directory.
///
/// File location: %AppData%\JPLens\settings.json
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "JPLens");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    // Fallback: settings.json shipped next to the executable (used on first run
    // before the user has ever saved to AppData).
    private static readonly string DefaultSettingsPath =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<SettingsService> _logger;

    public SettingsService(ILogger<SettingsService> logger)
        => _logger = logger;

    /// <summary>
    /// Loads settings from disk.
    /// Priority: %AppData%\JPLens\settings.json → app-directory settings.json → hardcoded defaults.
    /// </summary>
    public AppSettings Load()
    {
        // 1. User-persisted settings in AppData
        if (File.Exists(SettingsPath))
            return TryDeserialize(SettingsPath) ?? new AppSettings();

        // 2. Shipped default settings next to the executable
        if (File.Exists(DefaultSettingsPath))
        {
            _logger.LogInformation(
                "No user settings found; loading shipped defaults from '{P}'",
                DefaultSettingsPath);
            return TryDeserialize(DefaultSettingsPath) ?? new AppSettings();
        }

        // 3. Hardcoded defaults
        _logger.LogInformation("No settings file found; using hardcoded defaults");
        return new AppSettings();
    }

    private AppSettings? TryDeserialize(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read settings from '{P}'", path);
            return null;
        }
    }

    /// <summary>Persists <paramref name="settings"/> to disk.</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            _logger.LogDebug("Settings saved to '{P}'", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to '{P}'", SettingsPath);
        }
    }
}
