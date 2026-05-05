using System.IO;
using System.Text.Json;
using JapaneseOcr.Models;
using Microsoft.Extensions.Logging;

namespace JapaneseOcr.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the user's
/// application data directory.
///
/// File location: %AppData%\JapaneseOcr\settings.json
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "JapaneseOcr");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<SettingsService> _logger;

    public SettingsService(ILogger<SettingsService> logger)
        => _logger = logger;

    /// <summary>
    /// Loads settings from disk.  Returns defaults when the file doesn't exist
    /// or cannot be parsed.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            _logger.LogInformation("No settings file found; using defaults");
            return new AppSettings();
        }

        try
        {
            var json     = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read settings from '{P}'; using defaults", SettingsPath);
            return new AppSettings();
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
