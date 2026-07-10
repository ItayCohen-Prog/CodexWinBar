using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class GeminiParserTests
{
    private const string ParserType = "CodexWinBar.Providers.Gemini.GeminiQuotaParser";
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_excludes_primary_and_secondary_buckets_from_extras()
    {
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            ParserType,
            "Parse",
            """
            {
              "buckets": [
                { "modelId": "gemini-2.5-pro", "remainingFraction": 0.4 },
                { "modelId": "gemini-2.5-flash", "remainingFraction": 0.7 },
                { "modelId": "gemini-2.5-flash-lite", "remainingFraction": 0.9 }
              ]
            }
            """,
            Now,
            "me@example.com");

        Assert.Equal(ProviderId.Gemini, snapshot.Provider);
        // Pro is the primary window, flash the secondary; neither may repeat in extras.
        Assert.Equal(60, snapshot.Primary?.UsedPercent);
        Assert.Equal(30, snapshot.Secondary?.UsedPercent);
        var extra = Assert.Single(snapshot.ExtraWindows);
        Assert.Equal("gemini-2.5-flash-lite", extra.Id);
    }

    [Fact]
    public void Parse_excludes_fallback_primary_bucket_from_extras_when_no_pro_model()
    {
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            ParserType,
            "Parse",
            """
            {
              "buckets": [
                { "modelId": "gemini-exp-alpha", "remainingFraction": 0.2 },
                { "modelId": "gemini-exp-beta", "remainingFraction": 0.8 }
              ]
            }
            """,
            Now,
            null);

        // With no pro/flash buckets the lowest-remaining bucket is promoted to primary
        // and must not also appear in extras.
        Assert.Equal(80, snapshot.Primary?.UsedPercent);
        Assert.Null(snapshot.Secondary);
        var extra = Assert.Single(snapshot.ExtraWindows);
        Assert.Equal("gemini-exp-beta", extra.Id);
    }
}
