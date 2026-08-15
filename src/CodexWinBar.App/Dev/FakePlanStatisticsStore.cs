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
                    BuildWeeklySeries("extra:claude-weekly-fable", "Fable 5", now, 54, [31, 49, 44, 72, 38, 65, 53, 47, 69, 42, 58, 36, 20]),
                ],
            },
            [ProviderId.Copilot] = new()
            {
                Provider = ProviderId.Copilot,
                Series =
                [
                    BuildCycleSeries("premium-interactions", "Premium interactions", now, 43200, 30, 14, [44, 62, 79, 51, 88, 67]),
                    BuildCycleSeries("chat", "Chat", now, 43200, 30, 14, [31, 48, 66, 42, 71, 57]),
                    BuildCycleSeries("extra:completions", "Completions", now, 43200, 30, 14, [18, 28, 43, 22, 39, 33]),
                ],
            },
            [ProviderId.Gemini] = new()
            {
                Provider = ProviderId.Gemini,
                Series =
                [
                    BuildCycleSeries("pro", "Pro", now, 1440, 1, 370, [58, 72, 46, 83, 64, 77]),
                    BuildCycleSeries("flash", "Flash", now, 1440, 1, 370, [35, 54, 69, 42, 61, 49]),
                    BuildCycleSeries("extra:gemini-2.5-flash-lite", "gemini-2.5-flash-lite", now, 1440, 1, 370, [21, 38, 47, 29, 53, 34]),
                ],
            },
            [ProviderId.OpenRouter] = new()
            {
                Provider = ProviderId.OpenRouter,
                Series =
                [
                    BuildCycleSeries("key-limit", "Key limit", now, 0, 7, 54, [42, 65, 51, 79, 58, 71], resetKnown: false),
                ],
            },
            [ProviderId.OpenAIAdmin] = new()
            {
                Provider = ProviderId.OpenAIAdmin,
                Series = [BuildSpendSeries(now)],
            },
            [ProviderId.Zai] = new()
            {
                Provider = ProviderId.Zai,
                Series =
                [
                    BuildCycleSeries("token-limit", "Token limit", now, 18000, 13, 29, [61, 78, 53, 86, 69, 74]),
                    BuildSessionSeries("time-limit", "Time limit", now, [64, 48, 81, 57, 76, 69]),
                ],
            },
            [ProviderId.Cursor] = new()
            {
                Provider = ProviderId.Cursor,
                Series =
                [
                    BuildCycleSeries("included-plan", "Included plan", now, 43200, 30, 14, [49, 73, 58, 82, 67, 76]),
                    BuildCycleSeries("auto-composer", "Auto + Composer", now, 43200, 30, 14, [33, 51, 44, 63, 47, 59]),
                    BuildCycleSeries("extra:cursor-api", "API (models)", now, 43200, 30, 14, [16, 27, 39, 22, 35, 29]),
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
        => BuildCycleSeries(id, title, now, 10080, 7, cycles, peaks);

    private static PlanUsageSeries BuildCycleSeries(
        string id,
        string title,
        DateTimeOffset now,
        int windowMinutes,
        int cycleDays,
        int cycles,
        IReadOnlyList<double> peaks,
        bool resetKnown = true)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var currentCycle = cycleDays == 7
            ? today.AddDays(-(int)today.DayOfWeek)
            : today.AddDays(-(today.DayNumber % cycleDays));
        var samples = new List<PlanUsageSample>();
        for (var index = 0; index < cycles; index++)
        {
            var cycleStart = currentCycle.AddDays(-cycleDays * (cycles - index - 1));
            var reset = resetKnown ? LocalTime(cycleStart.AddDays(cycleDays), 0) : (DateTimeOffset?)null;
            var peak = peaks[index % peaks.Count];
            samples.Add(new PlanUsageSample
            {
                CapturedAt = LocalTime(cycleStart, 8),
                UsedPercent = 0,
                ResetsAt = reset,
            });

            var workDays = Enumerable.Range(0, cycleDays)
                .Select(offset => cycleStart.AddDays(offset))
                .Where(date => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                .ToArray();
            var workHours = Math.Max(1, workDays.Length * 8);
            var observation = 0;
            foreach (var date in workDays)
            {
                for (var hour = 9; hour <= 16; hour++)
                {
                    observation++;
                    samples.Add(new PlanUsageSample
                    {
                        CapturedAt = LocalTime(date, hour, 45),
                        UsedPercent = peak * observation / workHours,
                        ResetsAt = reset,
                    });
                }
            }
        }

        return new PlanUsageSeries
        {
            Id = id,
            Title = title,
            WindowMinutes = windowMinutes,
            Samples = samples,
        };
    }

    private static PlanUsageSeries BuildSpendSeries(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var samples = new List<PlanUsageSample>();
        var values = new[] { 8.4, 14.75, 22.1, 11.3, 27.85, 5.6, 18.2, 31.45, 16.9, 24.35 };
        for (var offset = 369; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var value = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? values[(date.DayNumber + values.Length) % values.Length] * 0.18
                : values[(date.DayNumber + values.Length) % values.Length];
            // Provider cost buckets are UTC days that start at UTC midnight, like the real ones.
            samples.Add(new PlanUsageSample
            {
                CapturedAt = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                UsedPercent = value,
            });
        }

        return new PlanUsageSeries
        {
            Id = "history:api-spend",
            Title = "API spend",
            WindowMinutes = 1440,
            MetricKind = PlanUsageMetricKind.ActivityValue,
            Unit = "USD",
            ScaleMaximum = 100,
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
