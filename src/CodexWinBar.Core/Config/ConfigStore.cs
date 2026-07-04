using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CodexWinBar.Core.Json;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Core.Config;

/// <summary>
/// Loads and saves the upstream-compatible config.json file.
/// </summary>
/// <param name="env">Environment lookup function.</param>
/// <param name="userProfileDir">User profile directory used for default config paths.</param>
/// <param name="log">Log sink for non-secret diagnostics.</param>
public sealed class ConfigStore(Func<string, string?> env, string userProfileDir, Action<string> log)
{
    private const string ConfigDirectoryName = "codexbar";
    private const string ConfigFileName = "config.json";

    /// <summary>
    /// Resolves the config path using upstream-compatible precedence.
    /// </summary>
    /// <returns>The config file path.</returns>
    public string ResolvePath()
    {
        var overridePath = env("CODEXBAR_CONFIG");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var xdgConfigHome = env("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, ConfigDirectoryName, ConfigFileName);
        }

        var currentPath = Path.Combine(userProfileDir, ".config", ConfigDirectoryName, ConfigFileName);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var legacyPath = Path.Combine(userProfileDir, ".codexbar", ConfigFileName);
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        return currentPath;
    }

    /// <summary>
    /// Loads the config, returning defaults when the file is missing or invalid.
    /// </summary>
    /// <returns>The loaded or default config.</returns>
    public CodexBarConfig Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            return CreateDefaultConfig();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.CodexBarConfig) ?? CreateDefaultConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log($"Failed to load config.json; using defaults without overwriting the file. {ex.GetType().Name}: {ex.Message}");
            return CreateDefaultConfig();
        }
    }

    /// <summary>
    /// Saves the config atomically and restricts the file ACL to the current user when possible.
    /// </summary>
    /// <param name="config">The config to save.</param>
    public void Save(CodexBarConfig config)
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
                JsonSerializer.Serialize(stream, config, CoreJsonContext.Default.CodexBarConfig);
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

        RestrictToCurrentUser(path);
    }

    /// <summary>
    /// Finds a provider entry by upstream config id, or returns an absent default entry.
    /// </summary>
    /// <param name="config">Config to search.</param>
    /// <param name="id">Provider id.</param>
    /// <returns>The matching config entry, or a default entry with only id set.</returns>
    public ProviderConfigEntry EntryFor(CodexBarConfig config, ProviderId id)
    {
        var configId = id.ConfigId();
        return config.Providers.FirstOrDefault(entry => string.Equals(entry.Id, configId, StringComparison.OrdinalIgnoreCase))
            ?? new ProviderConfigEntry { Id = configId };
    }

    /// <summary>
    /// Replaces an entry by id while preserving order, or appends it when new.
    /// </summary>
    /// <param name="config">Config to update.</param>
    /// <param name="updated">Updated provider entry.</param>
    /// <returns>A config with the entry patched in.</returns>
    public CodexBarConfig WithEntry(CodexBarConfig config, ProviderConfigEntry updated)
    {
        var entries = config.Providers.ToList();
        var index = entries.FindIndex(entry => string.Equals(entry.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            entries[index] = updated;
        }
        else
        {
            entries.Add(updated);
        }

        return config with { Providers = entries };
    }

    /// <summary>
    /// Normalizes quota warning thresholds according to amendment A5.
    /// </summary>
    /// <param name="window">The input warning window.</param>
    /// <returns>A warning window with clamped, distinct, descending thresholds.</returns>
    public static QuotaWarningWindow Normalize(QuotaWarningWindow? window)
    {
        var thresholds = (window?.Thresholds ?? [])
            .Select(threshold => Math.Clamp(threshold, 0, 99))
            .Distinct()
            .OrderDescending()
            .ToArray();

        if (thresholds.Length == 0)
        {
            thresholds = [.. QuotaWarningWindow.DefaultThresholds];
        }

        return (window ?? new QuotaWarningWindow()) with { Thresholds = thresholds };
    }

    private static CodexBarConfig CreateDefaultConfig() => new()
    {
        Providers = ProviderIds.All.Select(id => new ProviderConfigEntry
        {
            Id = id.ConfigId(),
            Enabled = id is ProviderId.Codex or ProviderId.Claude,
            Source = "auto",
        }).ToArray(),
    };

    private void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                log("Failed to restrict config.json ACL: current user SID is unavailable.");
                return;
            }

            var security = new FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            ApplyFileSecurity(path, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SystemException)
        {
            log($"Failed to restrict config.json ACL. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyFileSecurity(string path, FileSecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        const uint ownerSecurityInformation = 0x00000001;
        const uint discretionaryAclSecurityInformation = 0x00000004;
        if (!SetFileSecurity(path, ownerSecurityInformation | discretionaryAclSecurityInformation, descriptor))
        {
            throw new IOException($"SetFileSecurity failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(
        string lpFileName,
        uint securityInformation,
        byte[] pSecurityDescriptor);
}
