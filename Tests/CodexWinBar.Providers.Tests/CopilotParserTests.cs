using System.Text.Json;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class CopilotParserTests
{
    private const string ParserType = "CodexWinBar.Providers.Copilot.CopilotUsageParser";
    private const string AuthType = "CodexWinBar.Providers.Copilot.CopilotAuth";
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_does_not_duplicate_chat_between_secondary_and_extras()
    {
        var snapshot = (UsageSnapshot)ProviderParserReflection.Invoke(
            ParserType,
            "Parse",
            """
            {
              "copilot_plan": "individual",
              "quota_reset_date": "2026-08-01",
              "quota_snapshots": {
                "premium_interactions": { "entitlement": 300, "remaining": 150, "percent_remaining": 50 },
                "chat": { "entitlement": 100, "remaining": 25, "percent_remaining": 25 },
                "completions": { "entitlement": 4000, "remaining": 1000, "percent_remaining": 25 }
              }
            }
            """,
            Now);

        Assert.Equal(ProviderId.Copilot, snapshot.Provider);
        Assert.Equal(50, snapshot.Primary?.UsedPercent);
        // Chat is the Secondary window and must not be repeated in ExtraWindows.
        Assert.Equal(75, snapshot.Secondary?.UsedPercent);
        Assert.DoesNotContain(snapshot.ExtraWindows, w => w.Id == "chat");
        var completions = Assert.Single(snapshot.ExtraWindows);
        Assert.Equal("completions", completions.Id);
    }

    [Fact]
    public void GetInt_accepts_number_and_string_values_without_throwing()
    {
        using var document = JsonDocument.Parse(
            """{ "number": 5, "string": "7", "junk": "abc", "object": {} }""");
        var root = document.RootElement;

        Assert.Equal(5, (int?)ProviderParserReflection.Invoke(AuthType, "GetInt", root, "number"));
        // GitHub can return "interval":"5" as a string; this used to throw InvalidOperationException.
        Assert.Equal(7, (int?)ProviderParserReflection.Invoke(AuthType, "GetInt", root, "string"));
        Assert.Null((int?)ProviderParserReflection.Invoke(AuthType, "GetInt", root, "junk"));
        Assert.Null((int?)ProviderParserReflection.Invoke(AuthType, "GetInt", root, "object"));
        Assert.Null((int?)ProviderParserReflection.Invoke(AuthType, "GetInt", root, "missing"));
    }
}
