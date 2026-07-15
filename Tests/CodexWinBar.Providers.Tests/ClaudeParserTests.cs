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

    [Theory]
    [InlineData(null, "default_claude_max_20x", "Max")] // OAuth profile tier, no subscription type
    [InlineData("claude_max", null, "Max")]             // profile organization_type
    [InlineData("pro", null, "Pro")]
    [InlineData(null, "default_claude_pro", "Pro")]
    [InlineData(null, null, null)]                      // nothing known -> LoginMethod fallback in UI
    public void Parse_derives_plan_from_either_subscription_type_or_rate_limit_tier(
        string? subscriptionType, string? rateLimitTier, string? expectedPlan)
    {
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            "CodexWinBar.Providers.Claude.ClaudeUsageParser",
            "Parse",
            """{"five_hour": {"utilization": 10, "resets_at": 1782673200}}""",
            Now,
            subscriptionType,
            rateLimitTier);

        Assert.Equal(expectedPlan, snapshot.Identity?.Plan);
    }

    [Fact]
    public void Parse_malformed_json_throws_clean_JsonException()
    {
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(
            Assert.ThrowsAny<Exception>(() => Parse("{broken")));
    }

    [Fact]
    public void Credentials_without_expiry_are_not_treated_as_expired()
    {
        // ExpiresAt == null used to mean "always expired", forcing a full OAuth refresh and
        // credential-file rewrite on every fetch when the refresh response omitted expires_in.
        Assert.False(IsExpired(expiresAt: null, Now));
    }

    [Fact]
    public void Credentials_with_past_expiry_are_expired_and_future_expiry_are_not()
    {
        Assert.True(IsExpired(Now.AddMinutes(-1), Now));
        Assert.False(IsExpired(Now.AddMinutes(1), Now));
    }

    private static bool IsExpired(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var type = Type.GetType(
            "CodexWinBar.Providers.Claude.ClaudeCredentials, CodexWinBar.Providers",
            throwOnError: true)!;
        var credentials = Activator.CreateInstance(
            type,
            "access-token",
            "refresh-token",
            expiresAt,
            Array.Empty<string>(),
            null,
            null,
            new System.Text.Json.Nodes.JsonObject())!;
        return (bool)type.GetMethod("IsExpired")!.Invoke(credentials, [now])!;
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
