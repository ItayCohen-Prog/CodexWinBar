using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class PlanStatisticsStoreTests
{
    [Fact]
    public void Record_persists_and_reloads_provider_series()
    {
        using var temp = TempDir.Create();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 10, 15, 0, TimeSpan.Zero);
        using (var store = new PlanStatisticsStore(_ => { }, temp.Path))
        {
            store.Record(Snapshot(
                capturedAt,
                primary: Window(36, capturedAt.AddHours(4), 300),
                secondary: Window(62, capturedAt.AddDays(4), 10080)));
        }

        using var reloaded = new PlanStatisticsStore(_ => { }, temp.Path);
        var statistics = reloaded.Get(ProviderId.Codex);

        Assert.Collection(
            statistics.Series,
            session =>
            {
                Assert.Equal("session", session.Id);
                Assert.Equal(300, session.WindowMinutes);
                Assert.Equal(36, Assert.Single(session.Samples).UsedPercent);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Id);
                Assert.Equal(10080, weekly.WindowMinutes);
                Assert.Equal(62, Assert.Single(weekly.Samples).UsedPercent);
            });
    }

    [Fact]
    public void Record_coalesces_same_cycle_hour_to_peak()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        var capturedAt = new DateTimeOffset(2026, 8, 12, 10, 5, 0, TimeSpan.Zero);
        var reset = capturedAt.AddHours(4);

        store.Record(Snapshot(capturedAt, primary: Window(24, reset, 300)));
        store.Record(Snapshot(capturedAt.AddMinutes(20), primary: Window(19, reset, 300)));
        store.Record(Snapshot(capturedAt.AddMinutes(40), primary: Window(41, reset, 300)));

        var sample = Assert.Single(Assert.Single(store.Get(ProviderId.Codex).Series).Samples);
        Assert.Equal(41, sample.UsedPercent);
        Assert.Equal(capturedAt.AddMinutes(40), sample.CapturedAt);
    }

    [Fact]
    public void Record_keeps_reset_boundary_that_changes_inside_hour()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        var capturedAt = new DateTimeOffset(2026, 8, 12, 10, 5, 0, TimeSpan.Zero);

        store.Record(Snapshot(capturedAt, primary: Window(91, capturedAt.AddMinutes(20), 300)));
        store.Record(Snapshot(capturedAt.AddMinutes(30), primary: Window(4, capturedAt.AddHours(5), 300)));

        var samples = Assert.Single(store.Get(ProviderId.Codex).Series).Samples;
        Assert.Equal(2, samples.Count);
        Assert.Equal([91d, 4d], samples.Select(item => item.UsedPercent));
    }

    [Fact]
    public void Record_skips_synthetic_and_unknown_extra_windows()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        var capturedAt = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            capturedAt,
            primary: Window(20, capturedAt.AddHours(4), 300) with { IsSyntheticPlaceholder = true }) with
        {
            ExtraWindows =
            [
                new NamedRateWindow
                {
                    Id = "unknown",
                    Title = "Unknown",
                    UsageKnown = false,
                    Window = Window(80, capturedAt.AddDays(2), 10080),
                },
            ],
        };

        store.Record(snapshot);

        Assert.Empty(store.Get(ProviderId.Codex).Series);
    }

    [Fact]
    public void Record_preserves_claude_fable_weekly_series_from_scoped_limit()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        var capturedAt = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
        store.Record(new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            ExtraWindows =
            [
                new NamedRateWindow
                {
                    Id = "claude-weekly-fable",
                    Title = "Fable 5",
                    Window = Window(47, capturedAt.AddDays(4), 10080),
                },
            ],
            UpdatedAt = capturedAt,
            Confidence = DataConfidence.Exact,
        });

        var fable = Assert.Single(store.Get(ProviderId.Claude).Series);
        Assert.Equal("extra:claude-weekly-fable", fable.Id);
        Assert.Equal("Fable 5", fable.Title);
        Assert.Equal(10080, fable.WindowMinutes);
        Assert.Equal(47, Assert.Single(fable.Samples).UsedPercent);
    }

    [Fact]
    public void Record_keeps_valid_windows_without_reset_boundaries()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        store.Record(new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Primary = new RateWindow { UsedPercent = 42, WindowMinutes = 300 },
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var series = Assert.Single(store.Get(ProviderId.Codex).Series);
        Assert.Equal("session", series.Id);
        Assert.Equal(300, series.WindowMinutes);
        Assert.Null(Assert.Single(series.Samples).ResetsAt);
    }

    [Fact]
    public void Record_uses_provider_specific_lane_names_and_default_durations()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        var capturedAt = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        store.Record(new UsageSnapshot
        {
            Provider = ProviderId.Copilot,
            Primary = new RateWindow { UsedPercent = 28, ResetsAt = capturedAt.AddDays(10) },
            Secondary = new RateWindow { UsedPercent = 14, ResetsAt = capturedAt.AddDays(10) },
            UpdatedAt = capturedAt,
        });

        Assert.Collection(
            store.Get(ProviderId.Copilot).Series,
            premium =>
            {
                Assert.Equal("premium-interactions", premium.Id);
                Assert.Equal("Premium interactions", premium.Title);
                Assert.Equal(43200, premium.WindowMinutes);
            },
            chat =>
            {
                Assert.Equal("chat", chat.Id);
                Assert.Equal("Chat", chat.Title);
                Assert.Equal(43200, chat.WindowMinutes);
            });
    }

    [Fact]
    public void Record_persists_provider_supplied_activity_buckets_without_percent_semantics()
    {
        using var temp = TempDir.Create();
        var capturedAt = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        using (var store = new PlanStatisticsStore(_ => { }, temp.Path))
        {
            store.Record(new UsageSnapshot
            {
                Provider = ProviderId.OpenAIAdmin,
                HistoricalUsage =
                [
                    new HistoricalUsageSample
                    {
                        SeriesId = "api-spend",
                        SeriesTitle = "API spend",
                        CapturedAt = capturedAt,
                        Value = 12.45,
                        Unit = "USD",
                    },
                ],
                UpdatedAt = capturedAt.AddHours(1),
            });
        }

        using var reloaded = new PlanStatisticsStore(_ => { }, temp.Path);
        var series = Assert.Single(reloaded.Get(ProviderId.OpenAIAdmin).Series);
        Assert.Equal("history:api-spend", series.Id);
        Assert.Equal(PlanUsageMetricKind.ActivityValue, series.MetricKind);
        Assert.Equal("USD", series.Unit);
        Assert.Equal(100, series.ScaleMaximum);
        Assert.Equal(12.45, Assert.Single(series.Samples).UsedPercent);
    }

    [Fact]
    public void Record_keeps_credit_depletion_when_provider_has_no_timed_quota_window()
    {
        using var temp = TempDir.Create();
        using var store = new PlanStatisticsStore(_ => { }, temp.Path);
        var capturedAt = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        store.Record(new UsageSnapshot
        {
            Provider = ProviderId.OpenRouter,
            Credits = new CreditsSnapshot
            {
                Remaining = 15,
                Limit = 20,
                Unit = "credits",
                UpdatedAt = capturedAt,
            },
            UpdatedAt = capturedAt,
        });

        var series = Assert.Single(store.Get(ProviderId.OpenRouter).Series);
        Assert.Equal("credits", series.Id);
        Assert.Equal("Credit balance", series.Title);
        Assert.Equal(25, Assert.Single(series.Samples).UsedPercent);
    }

    private static UsageSnapshot Snapshot(
        DateTimeOffset capturedAt,
        RateWindow? primary = null,
        RateWindow? secondary = null) => new()
        {
            Provider = ProviderId.Codex,
            Primary = primary,
            Secondary = secondary,
            UpdatedAt = capturedAt,
            Confidence = DataConfidence.Exact,
        };

    private static RateWindow Window(double used, DateTimeOffset reset, int minutes) => new()
    {
        UsedPercent = used,
        ResetsAt = reset,
        WindowMinutes = minutes,
    };
}
