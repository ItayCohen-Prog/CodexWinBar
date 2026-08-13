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
                    BuildSeries("session", "Session", 300, now, 1800, [42, 68, 31, 84, 0, 57, 74, 46, 92, 12, 63, 51, 78, 0, 39, 71]),
                    BuildSeries("weekly", "Weekly", 10080, now, 54, [64, 81, 73, 55, 89, 67, 77, 48, 83, 61, 72, 45, 58]),
                    BuildSeries("extra:gpt-5-codex", "GPT-5.3-Codex", 10080, now, 54, [28, 46, 52, 39, 63, 49, 71, 32, 58, 44, 67, 36, 41]),
                ],
            },
            [ProviderId.Claude] = new()
            {
                Provider = ProviderId.Claude,
                Series =
                [
                    BuildSeries("session", "Session", 300, now, 1800, [76, 54, 89, 0, 67, 82, 48, 93, 18, 71, 64, 86, 0, 59, 79, 72]),
                    BuildSeries("weekly", "Weekly", 10080, now, 54, [71, 62, 88, 79, 56, 91, 73, 68, 84, 77, 59, 86, 52]),
                    BuildSeries("tertiary", "Opus weekly", 10080, now, 54, [31, 49, 44, 72, 38, 65, 53, 47, 69, 42, 58, 36, 20]),
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

    private static PlanUsageSeries BuildSeries(
        string id,
        string title,
        int windowMinutes,
        DateTimeOffset now,
        int cycles,
        IReadOnlyList<double> peaks)
    {
        var duration = TimeSpan.FromMinutes(windowMinutes);
        var currentReset = now.Add(duration - TimeSpan.FromTicks(now.UtcTicks % duration.Ticks));
        var samples = new List<PlanUsageSample>();
        for (var index = 0; index < cycles; index++)
        {
            var reverseIndex = cycles - index - 1;
            var reset = currentReset - (duration * reverseIndex);
            var peak = peaks[index % peaks.Count];
            samples.Add(new PlanUsageSample
            {
                CapturedAt = reset - TimeSpan.FromTicks((long)(duration.Ticks * 0.72)),
                UsedPercent = peak <= 0 ? 0 : Math.Max(2, peak * 0.34),
                ResetsAt = reset,
            });
            samples.Add(new PlanUsageSample
            {
                CapturedAt = reset - TimeSpan.FromTicks((long)(duration.Ticks * 0.08)),
                UsedPercent = peak,
                ResetsAt = reset,
            });
        }

        return new PlanUsageSeries
        {
            Id = id,
            Title = title,
            WindowMinutes = windowMinutes,
            Samples = samples,
        };
    }
}
