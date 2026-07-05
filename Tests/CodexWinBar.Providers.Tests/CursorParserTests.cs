using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class CursorParserTests
{
    private const string ParserType = "CodexWinBar.Providers.Cursor.CursorParser";

    [Fact]
    public void Parse_maps_plan_composer_api_windows_extra_usage_and_identity()
    {
        var now = new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            ParserType,
            "Parse",
            """
            {
              "planName": "Pro",
              "billingCycleEnd": "2026-08-01T00:00:00Z",
              "plan": { "used": 64, "limit": 100 },
              "composer": { "usedPercent": 38 },
              "api": { "usedPercent": 12 },
              "onDemand": { "cents": 840 }
            }
            """,
            """
            { "email": "me@example.com" }
            """,
            now);

        Assert.Equal(ProviderId.Cursor, snapshot.Provider);

        // Included-plan usage becomes the primary window (used/limit -> percent).
        Assert.Equal(64, snapshot.Primary?.UsedPercent);
        // The billing-cycle end drives every window's reset.
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), snapshot.Primary?.ResetsAt);

        // Auto + Composer becomes the secondary window (direct percent).
        Assert.Equal(38, snapshot.Secondary?.UsedPercent);

        // API (named-model) usage is surfaced as a percent extra window.
        var api = Assert.Single(snapshot.ExtraWindows, w => w.Id == "cursor-api");
        Assert.True(api.UsageKnown);
        Assert.Equal(12, api.Window.UsedPercent);

        // On-demand spend is a scalar dollar value (cents normalized), never a bar.
        var extra = Assert.Single(snapshot.ExtraWindows, w => w.Id == "cursor-extra-usage");
        Assert.False(extra.UsageKnown);
        Assert.Equal("$8.40 this cycle", extra.Window.ResetDescription);

        Assert.Equal("me@example.com", snapshot.Identity?.AccountEmail);
        Assert.Equal("Pro", snapshot.Identity?.Plan);
        Assert.Equal("Session cookie", snapshot.Identity?.LoginMethod);
        Assert.Equal(DataConfidence.Exact, snapshot.Confidence);
    }

    [Fact]
    public void Parse_tolerates_missing_identity_and_wraps_data_envelope()
    {
        var now = new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            ParserType,
            "Parse",
            """
            { "data": { "plan": { "usedPercent": 20 } } }
            """,
            null,
            now);

        Assert.Equal(20, snapshot.Primary?.UsedPercent);
        Assert.Null(snapshot.Secondary);
        Assert.Null(snapshot.Identity);
        Assert.Equal(DataConfidence.Exact, snapshot.Confidence);
    }

    [Fact]
    public void Parse_malformed_usage_json_throws_JsonException()
    {
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(Assert.ThrowsAny<Exception>(() =>
            ProviderParserReflection.Invoke(ParserType, "Parse", "{broken", null, DateTimeOffset.UnixEpoch)));
    }
}
