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
        Assert.Equal(WidgetSide.Left, settings.WidgetSide);
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
        File.WriteAllText(settingsPath, "{\"widgetSide\":\"right\"}");

        var settings = new UiSettingsStore(_ => { }, temp.Path).Load();

        AssertDefaults(settings, expectedWidgetSide: WidgetSide.Right);
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

    [Fact]
    public void UiSettings_defaults_show_pace_indicator_on()
    {
        Assert.True(new UiSettings().ShowPaceIndicator);
    }

    [Fact]
    public void UiSettingsStore_migrates_legacy_file_to_enable_pace_once()
    {
        using var temp = TempDir.Create();
        var settingsPath = Path.Combine(temp.Path, "CodexWinBar", "ui-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        // A file written before the migration existed: pace explicitly off, no schema version.
        File.WriteAllText(settingsPath, "{\"showPaceIndicator\":false}");

        var store = new UiSettingsStore(_ => { }, temp.Path);
        var migrated = store.Load();

        Assert.True(migrated.ShowPaceIndicator);
        Assert.Equal(1, migrated.SettingsVersion);

        // The migration is persisted, so a reload sees the current version and does not run again.
        var reloaded = store.Load();
        Assert.True(reloaded.ShowPaceIndicator);
        Assert.Equal(1, reloaded.SettingsVersion);
    }

    [Fact]
    public void UiSettingsStore_respects_pace_off_at_current_version()
    {
        using var temp = TempDir.Create();
        var settingsPath = Path.Combine(temp.Path, "CodexWinBar", "ui-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        // Already at the current schema version with pace deliberately off: it must stay off.
        File.WriteAllText(settingsPath, "{\"settingsVersion\":1,\"showPaceIndicator\":false}");

        var settings = new UiSettingsStore(_ => { }, temp.Path).Load();

        Assert.False(settings.ShowPaceIndicator);
        Assert.Equal(1, settings.SettingsVersion);
    }

    private static void AssertDefaults(UiSettings settings, WidgetSide expectedWidgetSide = WidgetSide.Left)
    {
        Assert.Equal(5, settings.RefreshCadenceMinutes);
        Assert.True(settings.ShowPaceIndicator);
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
