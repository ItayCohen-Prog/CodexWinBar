using System.Text.Json;
using CodexWinBar.Core.Json;

namespace CodexWinBar.Core.Config;

/// <summary>
/// Loads and saves Windows UI settings stored under application data.
/// </summary>
/// <param name="log">Log sink for non-secret diagnostics.</param>
/// <param name="baseDirectory">Optional storage root. Defaults to application data.</param>
public sealed class UiSettingsStore(Action<string> log, string? baseDirectory = null)
{
    private const string DirectoryName = "CodexWinBar";
    private const string FileName = "ui-settings.json";

    /// <summary>Current settings schema version. Bump when adding a one-time migration in <see cref="Migrate"/>.</summary>
    private const int CurrentSettingsVersion = 1;

    /// <summary>
    /// Loads UI settings, returning defaults when the file is missing or invalid.
    /// </summary>
    /// <returns>The loaded or default UI settings.</returns>
    public UiSettings Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            // Fresh install: in-memory defaults apply. Stamp the current version so a later Save never
            // makes the next launch re-run migrations (which would fight a deliberate user choice).
            return new UiSettings { SettingsVersion = CurrentSettingsVersion };
        }

        UiSettings settings;
        try
        {
            using (var stream = File.OpenRead(path))
            {
                settings = (JsonSerializer.Deserialize(stream, CoreJsonContext.Default.UiSettings) ?? new UiSettings()).Normalize();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log($"Failed to load ui-settings.json; using defaults. {ex.GetType().Name}: {ex.Message}");
            return new UiSettings { SettingsVersion = CurrentSettingsVersion };
        }

        if (Migrate(settings))
        {
            try
            {
                Save(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Failed to persist migrated ui-settings.json. {ex.GetType().Name}: {ex.Message}");
            }
        }

        return settings;
    }

    /// <summary>
    /// Applies one-time upgrades to settings written by older builds. Returns true when anything
    /// changed (so the caller persists the result). Each step is guarded by <see cref="UiSettings.SettingsVersion"/>.
    /// </summary>
    private static bool Migrate(UiSettings settings)
    {
        if (settings.SettingsVersion >= CurrentSettingsVersion)
        {
            return false;
        }

        // v1: the pace indicator shipped defaulting to off, so existing installs never showed the
        // projected-usage marks in the flyout or expanded widget. Turn it on once. This cannot override
        // a deliberate opt-out — with the old default already off, an explicit false was indistinguishable
        // from the default, so no one had meaningfully turned it off. Users can still toggle it back off.
        settings.ShowPaceIndicator = true;

        settings.SettingsVersion = CurrentSettingsVersion;
        return true;
    }

    /// <summary>
    /// Saves UI settings atomically.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    public void Save(UiSettings settings)
    {
        var path = ResolvePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, settings, CoreJsonContext.Default.UiSettings);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private string ResolvePath()
    {
        var root = baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, DirectoryName, FileName);
    }
}
