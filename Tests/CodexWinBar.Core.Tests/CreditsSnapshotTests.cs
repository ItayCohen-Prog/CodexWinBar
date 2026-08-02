using System.Text.Json;
using CodexWinBar.Core.Models;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class CreditsSnapshotTests
{
    [Fact]
    public void Legacy_json_without_kind_defaults_to_credits_semantics()
    {
        const string json = """
            {
              "Remaining": 12.5,
              "Limit": 20,
              "Unit": "USD",
              "UpdatedAt": "2026-08-01T00:00:00+00:00"
            }
            """;

        var snapshot = JsonSerializer.Deserialize<CreditsSnapshot>(json);

        Assert.NotNull(snapshot);
        Assert.Equal(CreditsSnapshotKind.Credits, snapshot.Kind);
        Assert.True(snapshot.SupportsDepletionSemantics);
    }

    [Fact]
    public void Default_credits_kind_preserves_legacy_serialized_shape()
    {
        var snapshot = new CreditsSnapshot
        {
            Remaining = 12.5,
            Limit = 20,
            Unit = "USD",
            UpdatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00+00:00"),
        };

        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("Kind", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportsDepletionSemantics", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Spend_kind_round_trips_and_disables_depletion_semantics()
    {
        var snapshot = new CreditsSnapshot
        {
            Remaining = 143.2,
            Unit = "USD (30d)",
            UpdatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00+00:00"),
            Kind = CreditsSnapshotKind.Spend,
        };

        var json = JsonSerializer.Serialize(snapshot);
        var roundTripped = JsonSerializer.Deserialize<CreditsSnapshot>(json);

        Assert.Contains("\"Kind\":\"Spend\"", json, StringComparison.Ordinal);
        Assert.NotNull(roundTripped);
        Assert.Equal(CreditsSnapshotKind.Spend, roundTripped.Kind);
        Assert.False(roundTripped.SupportsDepletionSemantics);
    }
}
