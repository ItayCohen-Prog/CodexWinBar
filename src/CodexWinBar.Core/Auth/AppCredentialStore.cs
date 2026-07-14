using System.Security.Cryptography;
using System.Text;

namespace CodexWinBar.Core.Auth;

/// <summary>
/// App-owned credential storage: one DPAPI-encrypted (current-user scope) JSON document per provider
/// under %LOCALAPPDATA%\CodexWinBarData\credentials. Providers sign in through the app and store tokens
/// here; the app never reads other tools' credential files.
/// </summary>
public sealed class AppCredentialStore(Func<string, string?> env, Action<string>? log = null)
{
    private const string DirectoryOverrideVariable = "CODEXWINBAR_CREDENTIALS_DIR";

    /// <summary>Resolves the credentials directory (CODEXWINBAR_CREDENTIALS_DIR overrides the default).</summary>
    public string ResolveDirectory()
    {
        var overrideDir = env(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir.Trim();
        }

        var localAppData = env("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var directory = Path.Combine(localAppData, "CodexWinBarData", "credentials");
        MigrateLegacyCredentials(Path.Combine(localAppData, "CodexWinBar", "credentials"), directory);
        return directory;
    }

    /// <summary>Whether a credential document exists for <paramref name="name"/>.</summary>
    public bool Exists(string name) => File.Exists(this.PathFor(name));

    /// <summary>Loads and decrypts the credential JSON, or null when missing or undecryptable.</summary>
    public string? Load(string name)
    {
        var path = this.PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var raw = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Credential '{name}' could not be read. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Encrypts and atomically writes the credential JSON.</summary>
    public void Save(string name, string json)
    {
        var path = this.PathFor(name);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, protectedBytes);
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
            TryDelete(tempPath);
        }
    }

    /// <summary>Deletes the credential document if present (sign-out).</summary>
    public void Delete(string name) => TryDelete(this.PathFor(name));

    private string PathFor(string name) => Path.Combine(this.ResolveDirectory(), name + ".dat");

    private static void MigrateLegacyCredentials(string legacyDirectory, string directory)
    {
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            foreach (var legacyPath in Directory.EnumerateFiles(legacyDirectory, "*.dat", SearchOption.TopDirectoryOnly))
            {
                var destination = Path.Combine(directory, Path.GetFileName(legacyPath));
                if (!File.Exists(destination))
                {
                    File.Copy(legacyPath, destination);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed migration must not stop startup. The installer also performs this copy before
            // replacing older Velopack installs, where the legacy directory lived under the app root.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }
}
