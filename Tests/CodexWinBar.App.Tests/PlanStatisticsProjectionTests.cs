using CodexWinBar.App.Statistics;
using CodexWinBar.Core.Statistics;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class PlanStatisticsProjectionTests
{
    [Fact]
    public void Build_groups_observations_by_reset_and_excludes_current_from_benchmark()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var series = new PlanUsageSeries
        {
            Id = "weekly",
            Title = "Weekly",
            WindowMinutes = 10080,
            Samples =
            [
                Sample(now.AddDays(-9), 20, now.AddDays(-7)),
                Sample(now.AddDays(-8), 70, now.AddDays(-7)),
                Sample(now.AddDays(-3), 30, now.AddHours(4)),
                Sample(now.AddHours(-1), 40, now.AddHours(4)),
            ],
        };

        var summary = PlanStatisticsProjection.Build(series, now);

        Assert.Equal(2, summary.Cycles.Count);
        Assert.Equal(40, summary.CurrentUsedPercent);
        Assert.Equal(70, summary.AveragePeakPercent);
        Assert.Equal(70, summary.HighestPeakPercent);
        Assert.Equal(30, summary.AverageUnusedPercent);
    }

    [Fact]
    public void Build_groups_reset_times_across_two_minute_bucket_boundary()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var reset = new DateTimeOffset(2026, 8, 13, 12, 1, 59, TimeSpan.Zero);
        var series = new PlanUsageSeries
        {
            Id = "session",
            Title = "Session",
            WindowMinutes = 300,
            Samples =
            [
                Sample(now.AddMinutes(-10), 20, reset),
                Sample(now, 44, reset.AddSeconds(2)),
            ],
        };

        var summary = PlanStatisticsProjection.Build(series, now);

        Assert.Single(summary.Cycles);
        Assert.Equal(44, summary.CurrentUsedPercent);
        Assert.Null(summary.AveragePeakPercent);
    }

    [Fact]
    public void Build_caps_chart_at_thirty_cycles()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var series = new PlanUsageSeries
        {
            Id = "session",
            Title = "Session",
            WindowMinutes = 300,
            Samples = Enumerable.Range(1, 42)
                .Select(index => Sample(now.AddHours(-index), index, now.AddHours(-index + 1)))
                .ToArray(),
        };

        var summary = PlanStatisticsProjection.Build(series, now);

        Assert.Equal(30, summary.Cycles.Count);
    }

    private static PlanUsageSample Sample(DateTimeOffset captured, double used, DateTimeOffset reset) => new()
    {
        CapturedAt = captured,
        UsedPercent = used,
        ResetsAt = reset,
    };
}
