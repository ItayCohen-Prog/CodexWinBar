using Microsoft.Win32;

namespace CodexWinBar.App.Startup;

/// <summary>Manages CodexWinBar launch-at-login registration in HKCU Run.</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexWinBar";

    /// <summary>Returns whether CodexWinBar is registered to launch at sign-in.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && IsCurrentProcessPath(value);
    }

    /// <summary>Enables or disables CodexWinBar launch at sign-in.</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Quote(Environment.ProcessPath ?? AppContext.BaseDirectory));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static bool IsCurrentProcessPath(string value)
    {
        var expected = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(value.Trim().Trim('"'), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
