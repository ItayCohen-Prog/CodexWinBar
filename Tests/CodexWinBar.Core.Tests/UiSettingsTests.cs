using CodexWinBar.Core.Config;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class UiSettingsTests
{
    [Fact]
    public void UiSettings_defaults_match_windows_contract()
    {
        var settings = new UiSettings();

        Assert.Equal(5, settings.RefreshCadenceMinutes);
        Assert.True(settings.MergeIcons);
        Assert.Equal(DisplayTextMode.Percent, settings.DisplayTextMode);
        Assert.False(settings.UsageBarsShowUsed);
        Assert.False(settings.ResetTimesShowAbsolute);
        Assert.False(settings.LaunchAtLogin);
        Assert.True(settings.StatusChecksEnabled);
        Assert.True(settings.QuotaNotificationsEnabled);
        Assert.Equal(WidgetMode.Auto, settings.WidgetMode);
        Assert.Equal(WidgetSide.Right, settings.WidgetSide);
    }

    [Fact]
    public void UiSettingsStore_returns_defaults_for_corrupt_file()
    {
        using var temp = TempDir.Create();
        var settingsPath = Path.Combine(temp.Path, "CodexWinBar", "ui-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{not json");
        var settings = new UiSettingsStore(_ => { }, temp.Path).Load();

        AssertDefaults(settings);
    }

    [Fact]
    public void UiSettingsStore_partial_json_preserves_documented_defaults()
    {
        using var temp = TempDir.Create();
        var settingsPath = Path.Combine(temp.Path, "CodexWinBar", "ui-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{\"widgetSide\":\"left\"}");

        var settings = new UiSettingsStore(_ => { }, temp.Path).Load();

        AssertDefaults(settings, expectedWidgetSide: WidgetSide.Left);
    }

    private static void AssertDefaults(UiSettings settings, WidgetSide expectedWidgetSide = WidgetSide.Right)
    {
        Assert.Equal(5, settings.RefreshCadenceMinutes);
        Assert.True(settings.MergeIcons);
        Assert.Equal(DisplayTextMode.Percent, settings.DisplayTextMode);
        Assert.False(settings.UsageBarsShowUsed);
        Assert.False(settings.ResetTimesShowAbsolute);
        Assert.False(settings.LaunchAtLogin);
        Assert.True(settings.StatusChecksEnabled);
        Assert.True(settings.QuotaNotificationsEnabled);
        Assert.Equal(WidgetMode.Auto, settings.WidgetMode);
        Assert.Equal(expectedWidgetSide, settings.WidgetSide);
    }
}
