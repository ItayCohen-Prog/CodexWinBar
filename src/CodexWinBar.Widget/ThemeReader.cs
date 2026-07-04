using Microsoft.Win32;

namespace CodexWinBar.Widget;

/// <summary>Reads taskbar-relevant personalization settings from the current user registry hive.</summary>
public sealed class ThemeReader
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private bool _systemUsesLightTheme;
    private bool _appsUseLightTheme;
    private bool _enableTransparency;

    /// <summary>Initializes a new theme reader and captures the current registry values.</summary>
    public ThemeReader()
    {
        Read();
    }

    /// <summary>True when the Windows system surfaces use the light theme.</summary>
    public bool SystemUsesLightTheme => _systemUsesLightTheme;

    /// <summary>True when app surfaces use the light theme.</summary>
    public bool AppsUseLightTheme => _appsUseLightTheme;

    /// <summary>True when Windows transparency effects are enabled.</summary>
    public bool EnableTransparency => _enableTransparency;

    /// <summary>Raised when <see cref="NotifySettingChanged"/> observes a changed value.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads personalization settings after WM_SETTINGCHANGE.</summary>
    public void NotifySettingChanged()
    {
        bool oldSystem = _systemUsesLightTheme;
        bool oldApps = _appsUseLightTheme;
        bool oldTransparency = _enableTransparency;
        Read();
        if (oldSystem != _systemUsesLightTheme || oldApps != _appsUseLightTheme || oldTransparency != _enableTransparency)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        _systemUsesLightTheme = ReadBool(key, "SystemUsesLightTheme", defaultValue: false);
        _appsUseLightTheme = ReadBool(key, "AppsUseLightTheme", defaultValue: true);
        _enableTransparency = ReadBool(key, "EnableTransparency", defaultValue: true);
    }

    private static bool ReadBool(RegistryKey? key, string name, bool defaultValue)
    {
        object? value = key?.GetValue(name);
        return value is int intValue ? intValue != 0 : defaultValue;
    }
}
