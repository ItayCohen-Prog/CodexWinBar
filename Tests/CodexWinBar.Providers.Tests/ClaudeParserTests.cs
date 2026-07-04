using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class ClaudeParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_maps_five_hour_seven_day_and_opus_windows()
    {
        var snapshot = Parse("""
            {
              "five_hour": {"utilization": 15.5, "resets_at": 1782673200},
              "seven_day": {"utilization": 45, "resets_at": "2026-07-11T10:00:00-07:00"},
              "seven_day_opus": {"utilization": 70, "resets_at": "1783267200000"},
              "extra_usage": {"is_enabled": true, "used_credits": 3, "monthly_limit": 10, "currency": "USD"}
            }
            """);

        Assert.Equal(ProviderId.Claude, snapshot.Provider);
        Assert.Equal(300, snapshot.Primary?.WindowMinutes);
        Assert.Equal(15.5, snapshot.Primary?.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1782673200), snapshot.Primary?.ResetsAt);
        Assert.Equal(10080, snapshot.Secondary?.WindowMinutes);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 17, 0, 0, TimeSpan.Zero), snapshot.Secondary?.ResetsAt);
        Assert.Equal(10080, snapshot.Tertiary?.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1783267200000), snapshot.Tertiary?.ResetsAt);
        Assert.Equal("Max", snapshot.Identity?.Plan);
        Assert.Equal(7, snapshot.Credits?.Remaining);
        Assert.Equal(10, snapshot.Credits?.Limit);
        Assert.Equal("USD", snapshot.Credits?.Unit);
    }

    [Fact]
    public void Parse_promotes_weekly_to_primary_when_five_hour_missing()
    {
        var snapshot = Parse("""
            {
              "seven_day": {"utilization": 45, "resets_at": 1783267200}
            }
            """);

        Assert.NotNull(snapshot.Primary);
        Assert.Equal(10080, snapshot.Primary.WindowMinutes);
        Assert.Equal(45, snapshot.Primary.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783267200), snapshot.Primary.ResetsAt);
        Assert.Null(snapshot.Secondary);
    }

    [Fact]
    public void Parse_maps_sonnet_and_routines_extra_windows()
    {
        var snapshot = Parse("""
            {
              "five_hour": {"utilization": 15, "resets_at": 1782673200},
              "seven_day_sonnet": {"utilization": 55, "resets_at": 1783267200},
              "seven_day_cowork": {"utilization": 65, "resets_at": 1783267200}
            }
            """);

        Assert.Contains(snapshot.ExtraWindows, w => w.Id == "claude-sonnet-weekly" && w.Window.UsedPercent == 55);
        Assert.Contains(snapshot.ExtraWindows, w => w.Id == "claude-routines" && w.Window.UsedPercent == 65);
    }

    [Fact]
    public void Parse_malformed_json_throws_clean_JsonException()
    {
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(
            Assert.ThrowsAny<Exception>(() => Parse("{broken")));
    }

    private static UsageSnapshot Parse(string json) =>
        (UsageSnapshot)ProviderParserReflection.Invoke(
            "CodexWinBar.Providers.Claude.ClaudeUsageParser",
            "Parse",
            json,
            Now,
            "max_20x",
            "tier_1");
}
