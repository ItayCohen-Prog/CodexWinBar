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

    /// <summary>
    /// Loads UI settings, returning defaults when the file is missing or invalid.
    /// </summary>
    /// <returns>The loaded or default UI settings.</returns>
    public UiSettings Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            return new UiSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var settings = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.UiSettings) ?? new UiSettings();
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log($"Failed to load ui-settings.json; using defaults. {ex.GetType().Name}: {ex.Message}");
            return new UiSettings();
        }
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
