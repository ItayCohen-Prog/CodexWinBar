using CodexWinBar.Core.Statistics;

namespace CodexWinBar.App.Statistics;

internal sealed record PlanCycle(
    DateTimeOffset CycleEndsAt,
    double PeakUsedPercent,
    double LatestUsedPercent,
    bool IsCurrent,
    int ObservationCount);

internal sealed record PlanStatisticsSummary(
    IReadOnlyList<PlanCycle> Cycles,
    double? CurrentUsedPercent,
    double? AveragePeakPercent,
    double? HighestPeakPercent,
    double? AverageUnusedPercent);

internal static class PlanStatisticsProjection
{
    private const int MaxVisibleCycles = 30;

    internal static PlanStatisticsSummary Build(PlanUsageSeries series, DateTimeOffset now)
    {
        var groups = GroupByEquivalentReset(series.Samples)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.CapturedAt).ToArray();
                var reset = ordered.Max(item => item.ResetsAt!.Value);
                return new PlanCycle(
                    reset,
                    ordered.Max(item => item.UsedPercent),
                    ordered[^1].UsedPercent,
                    reset > now,
                    ordered.Length);
            })
            .OrderBy(item => item.CycleEndsAt)
            .TakeLast(MaxVisibleCycles)
            .ToArray();

        if (groups.Length == 0)
        {
            return new PlanStatisticsSummary([], null, null, null, null);
        }

        var completed = groups.Where(item => !item.IsCurrent).ToArray();
        var current = groups.LastOrDefault(item => item.IsCurrent);
        var average = completed.Length > 0 ? completed.Average(item => item.PeakUsedPercent) : (double?)null;
        return new PlanStatisticsSummary(
            groups,
            current?.LatestUsedPercent,
            average,
            completed.Length > 0 ? completed.Max(item => item.PeakUsedPercent) : null,
            average is { } value ? Math.Max(0, 100 - value) : null);
    }

    private static IReadOnlyList<IReadOnlyList<PlanUsageSample>> GroupByEquivalentReset(
        IReadOnlyList<PlanUsageSample> samples)
    {
        var groups = new List<List<PlanUsageSample>>();
        foreach (var sample in samples
            .Where(item => item.ResetsAt is not null)
            .OrderBy(item => item.ResetsAt))
        {
            var existing = groups.LastOrDefault(group =>
                Math.Abs((group[0].ResetsAt!.Value - sample.ResetsAt!.Value).TotalMinutes) < 2);
            if (existing is null)
            {
                groups.Add([sample]);
            }
            else
            {
                existing.Add(sample);
            }
        }

        return groups;
    }
}
