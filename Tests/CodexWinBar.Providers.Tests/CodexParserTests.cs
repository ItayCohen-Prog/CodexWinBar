using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class CodexParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseUsage_maps_windows_by_duration_and_epoch_resets_to_utc()
    {
        var snapshot = ParseUsage("""
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {"used_percent": 71.5, "limit_window_seconds": 604800, "reset_at": 1783267200},
                "secondary_window": {"used_percent": 25, "limit_window_seconds": 18000, "reset_at": 1782673200}
              },
              "credits": {"balance": 12.5},
              "individual_limit": {"limit": 50}
            }
            """);

        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Equal(300, snapshot.Primary?.WindowMinutes);
        Assert.Equal(25, snapshot.Primary?.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1782673200), snapshot.Primary?.ResetsAt);
        Assert.Equal(10080, snapshot.Secondary?.WindowMinutes);
        Assert.Equal(71.5, snapshot.Secondary?.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783267200), snapshot.Secondary?.ResetsAt);
        Assert.Equal(12.5, snapshot.Credits?.Remaining);
        Assert.Equal(50, snapshot.Credits?.Limit);
        Assert.Equal("plus", snapshot.Identity?.Plan);
    }

    [Fact]
    public void ParseUsage_tolerates_missing_windows_and_places_single_weekly_as_secondary()
    {
        var snapshot = ParseUsage("""
            {
              "rate_limit": {
                "primary_window": {"used_percent": 40, "limit_window_seconds": 604800, "reset_at": 1783267200}
              }
            }
            """);

        Assert.Null(snapshot.Primary);
        Assert.Equal(10080, snapshot.Secondary?.WindowMinutes);
        Assert.Equal(40, snapshot.Secondary?.UsedPercent);
    }

    [Fact]
    public void ParseUsage_maps_additional_rate_limits_as_extra_windows()
    {
        var snapshot = ParseUsage("""
            {
              "rate_limit": {
                "primary_window": {"used_percent": 10, "limit_window_seconds": 18000}
              },
              "additional_rate_limits": [
                {
                  "limit_name": "GPT-5 Thinking",
                  "metered_feature": "gpt5-thinking",
                  "rate_limit": {
                    "primary_window": {"used_percent": 88, "limit_window_seconds": 604800, "reset_at": 1783267200}
                  }
                }
              ]
            }
            """);

        var extra = Assert.Single(snapshot.ExtraWindows);
        Assert.Equal("codex-gpt5-thinking", extra.Id);
        Assert.Equal("GPT-5 Thinking", extra.Title);
        Assert.Equal(10080, extra.Window.WindowMinutes);
        Assert.Equal(88, extra.Window.UsedPercent);
    }

    [Fact]
    public void ParseUsage_malformed_json_throws_clean_JsonException()
    {
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(
            Assert.ThrowsAny<Exception>(() => ParseUsage("{broken")));
    }

    private static UsageSnapshot ParseUsage(string json) =>
        (UsageSnapshot)ProviderParserReflection.Invoke(
            "CodexWinBar.Providers.Codex.CodexUsageParser",
            "ParseUsage",
            json,
            Now,
            null,
            null);
}
