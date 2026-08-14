using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;

namespace CodexWinBar.App.Dev;

/// <summary>Deterministic multi-cycle history for visual QA in fake-data mode.</summary>
internal sealed class FakePlanStatisticsStore : IPlanStatisticsStore
{
    private readonly IReadOnlyDictionary<ProviderId, ProviderPlanStatistics> values;

    public FakePlanStatisticsStore()
    {
        var now = DateTimeOffset.UtcNow;
        this.values = new Dictionary<ProviderId, ProviderPlanStatistics>
        {
            [ProviderId.Codex] = new()
            {
                Provider = ProviderId.Codex,
                Series =
                [
                    BuildWeeklySeries("weekly", "Weekly", now, 54, [64, 81, 73, 55, 89, 67, 77, 48, 83, 61, 72, 45, 58]),
                ],
            },
            [ProviderId.Claude] = new()
            {
                Provider = ProviderId.Claude,
                Series =
                [
                    BuildSessionSeries("session", "Session", now, [76, 54, 89, 67, 82, 48, 93, 71, 64, 86, 59, 79, 72]),
                    BuildWeeklySeries("weekly", "Weekly", now, 54, [71, 62, 88, 79, 56, 91, 73, 68, 84, 77, 59, 86, 52]),
                    BuildWeeklySeries("tertiary", "Opus weekly", now, 54, [31, 49, 44, 72, 38, 65, 53, 47, 69, 42, 58, 36, 20]),
                ],
            },
        };
    }

    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public ProviderPlanStatistics Get(ProviderId provider) => this.values.TryGetValue(provider, out var value)
        ? value
        : new ProviderPlanStatistics { Provider = provider };

    public void Record(Core.Models.UsageSnapshot snapshot)
    {
    }

    public void Dispose()
    {
    }

    private static PlanUsageSeries BuildWeeklySeries(
        string id,
        string title,
        DateTimeOffset now,
        int cycles,
        IReadOnlyList<double> peaks)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var currentWeek = today.AddDays(-(int)today.DayOfWeek);
        var samples = new List<PlanUsageSample>();
        for (var index = 0; index < cycles; index++)
        {
            var weekStart = currentWeek.AddDays(-7 * (cycles - index - 1));
            var reset = LocalTime(weekStart.AddDays(7), 0);
            var peak = peaks[index % peaks.Count];
            samples.Add(new PlanUsageSample
            {
                CapturedAt = LocalTime(weekStart, 8),
                UsedPercent = 0,
                ResetsAt = reset,
            });

            const int workHoursPerWeek = 5 * 8;
            var observation = 0;
            for (var day = 1; day <= 5; day++)
            {
                for (var hour = 9; hour <= 16; hour++)
                {
                    observation++;
                    samples.Add(new PlanUsageSample
                    {
                        CapturedAt = LocalTime(weekStart.AddDays(day), hour, 45),
                        UsedPercent = peak * observation / workHoursPerWeek,
                        ResetsAt = reset,
                    });
                }
            }
        }

        return new PlanUsageSeries
        {
            Id = id,
            Title = title,
            WindowMinutes = 10080,
            Samples = samples,
        };
    }

    private static PlanUsageSeries BuildSessionSeries(
        string id,
        string title,
        DateTimeOffset now,
        IReadOnlyList<double> peaks)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var samples = new List<PlanUsageSample>();
        var peakIndex = 0;
        for (var date = today.AddDays(-(52 * 7)); date <= today; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var (startsAt, resetsAt) in new[] { (8, 13), (13, 18) })
            {
                var reset = LocalTime(date, resetsAt);
                var peak = peaks[peakIndex++ % peaks.Count];
                for (var offset = 0; offset <= 4; offset++)
                {
                    samples.Add(new PlanUsageSample
                    {
                        CapturedAt = LocalTime(date, startsAt + offset, offset == 4 ? 45 : 5),
                        UsedPercent = peak * offset / 4,
                        ResetsAt = reset,
                    });
                }
            }
        }

        return new PlanUsageSeries
        {
            Id = id,
            Title = title,
            WindowMinutes = 300,
            Samples = samples,
        };
    }

    private static DateTimeOffset LocalTime(DateOnly date, int hour, int minute = 0) =>
        new(date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Local));
}
