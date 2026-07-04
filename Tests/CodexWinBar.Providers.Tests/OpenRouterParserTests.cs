using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class OpenRouterParserTests
{
    [Fact]
    public void Parse_calculates_credit_remaining_and_key_limit_percent()
    {
        var now = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            "CodexWinBar.Providers.OpenRouter.OpenRouterParser",
            "Parse",
            """
            {"data": {"total_credits": 25.5, "total_usage": 5.25}}
            """,
            """
            {"data": {"limit": 100, "usage": 12.5, "limit_remaining": 87.5, "label": "Team Key"}}
            """,
            now);

        Assert.Equal(ProviderId.OpenRouter, snapshot.Provider);
        Assert.Equal(20.25, snapshot.Credits?.Remaining);
        Assert.Equal(25.5, snapshot.Credits?.Limit);
        Assert.Equal(12.5, snapshot.Primary?.UsedPercent);
        Assert.Equal("Limit remaining: 87.5", snapshot.Primary?.ResetDescription);
        Assert.Equal("Team Key", snapshot.Identity?.AccountOrganization);
        Assert.Equal(now, snapshot.UpdatedAt);
    }

    [Fact]
    public void Parse_malformed_json_throws_clean_JsonException()
    {
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(Assert.ThrowsAny<Exception>(() =>
            ProviderParserReflection.Invoke(
                "CodexWinBar.Providers.OpenRouter.OpenRouterParser",
                "Parse",
                "{broken",
                """{"data": {}}""",
                DateTimeOffset.UnixEpoch)));
    }
}
