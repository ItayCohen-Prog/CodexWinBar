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
        Assert.Equal([20], settings.QuotaSessionThresholds);
        Assert.True(settings.QuotaSessionEnabled);
        Assert.Equal([20], settings.QuotaWeeklyThresholds);
        Assert.True(settings.QuotaWeeklyEnabled);
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

    [Fact]
    public void UiSettingsStore_normalizes_invalid_persisted_values()
    {
        using var temp = TempDir.Create();
        var settingsPath = Path.Combine(temp.Path, "CodexWinBar", "ui-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "refreshCadence": -3,
              "quotaSessionThresholds": null,
              "quotaWeeklyThresholds": [150, -5, 30, 30],
              "displayTextMode": 42
            }
            """);

        var settings = new UiSettingsStore(_ => { }, temp.Path).Load();

        Assert.Equal(5, settings.RefreshCadenceMinutes);
        Assert.Equal([20], settings.QuotaSessionThresholds);
        Assert.Equal([99, 30, 0], settings.QuotaWeeklyThresholds);
        Assert.Equal(DisplayTextMode.Percent, settings.DisplayTextMode);
    }

    [Fact]
    public void UiSettingsStore_preserves_manual_cadence_and_empty_thresholds()
    {
        using var temp = TempDir.Create();
        var settingsPath = Path.Combine(temp.Path, "CodexWinBar", "ui-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{\"refreshCadence\":null,\"quotaSessionThresholds\":[]}");

        var settings = new UiSettingsStore(_ => { }, temp.Path).Load();

        Assert.Null(settings.RefreshCadenceMinutes);
        Assert.Empty(settings.QuotaSessionThresholds);
        Assert.Equal([20], settings.QuotaWeeklyThresholds);
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
        Assert.Equal([20], settings.QuotaSessionThresholds);
        Assert.True(settings.QuotaSessionEnabled);
        Assert.Equal([20], settings.QuotaWeeklyThresholds);
        Assert.True(settings.QuotaWeeklyEnabled);
        Assert.Equal(WidgetMode.Auto, settings.WidgetMode);
        Assert.Equal(expectedWidgetSide, settings.WidgetSide);
    }
}
