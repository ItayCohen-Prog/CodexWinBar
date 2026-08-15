using CodexWinBar.Core.Statistics;

namespace CodexWinBar.App.Statistics;

internal sealed record ActivityHour(int Hour, double Value, int ObservationCount);

internal sealed record ActivityDay(
    DateOnly Date,
    double Value,
    int ActiveHours,
    int ObservationCount,
    IReadOnlyList<ActivityHour> Hours,
    double Intensity)
{
    internal bool HasCoverage => this.ObservationCount > 0;
}

internal sealed record ActivityWeek(
    DateOnly StartsOn,
    IReadOnlyList<ActivityDay> Days,
    double Total,
    int ActiveDays,
    int CoveredDays,
    ActivityDay? BusiestDay);

internal sealed record ActivityMonth(
    DateOnly StartsOn,
    DateOnly EndsOn,
    IReadOnlyList<ActivityDay> Days,
    IReadOnlyList<ActivityWeek> Weeks,
    double Total,
    int ActiveDays,
    int CoveredDays,
    double DailyAverage,
    ActivityDay? BusiestDay,
    ActivityWeek? BusiestWeek);

internal sealed record ActivityOverview(
    IReadOnlyList<ActivityDay> Days,
    double Total,
    int ActiveDays,
    int CoveredDays,
    double DailyAverage,
    ActivityDay? BusiestDay,
    DateOnly StartsOn,
    DateOnly EndsOn)
{
    internal ActivityDay? Day(DateOnly date) => this.Days.FirstOrDefault(item => item.Date == date);

    internal ActivityWeek WeekContaining(DateOnly date)
    {
        var start = PlanStatisticsProjection.WeekStart(date);
        var days = this.Days.Where(item => item.Date >= start && item.Date <= start.AddDays(6)).ToArray();
        var active = days.Where(item => item.Value > 0.001).ToArray();
        return new ActivityWeek(
            start,
            days,
            days.Sum(item => item.Value),
            active.Length,
            days.Count(item => item.HasCoverage),
            active.OrderByDescending(item => item.Value).FirstOrDefault());
    }

    internal ActivityMonth MonthContaining(DateOnly date)
    {
        var start = new DateOnly(date.Year, date.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var days = this.Days.Where(item => item.Date >= start && item.Date <= end && item.Date <= this.EndsOn).ToArray();
        var weeks = new List<ActivityWeek>();
        for (var weekStart = PlanStatisticsProjection.WeekStart(start);
             weekStart <= PlanStatisticsProjection.WeekStart(end);
             weekStart = weekStart.AddDays(7))
        {
            var visibleDays = this.WeekContaining(weekStart).Days
                .Where(item => item.Date >= start && item.Date <= end && item.Date <= this.EndsOn)
                .ToArray();
            var activeWeekDays = visibleDays.Where(item => item.Value > 0.001).ToArray();
            weeks.Add(new ActivityWeek(
                weekStart,
                visibleDays,
                visibleDays.Sum(item => item.Value),
                activeWeekDays.Length,
                visibleDays.Count(item => item.HasCoverage),
                activeWeekDays.OrderByDescending(item => item.Value).FirstOrDefault()));
        }

        var active = days.Where(item => item.Value > 0.001).ToArray();
        var coveredDays = days.Count(item => item.HasCoverage);
        return new ActivityMonth(
            start,
            end,
            days,
            weeks,
            days.Sum(item => item.Value),
            active.Length,
            coveredDays,
            coveredDays == 0 ? 0 : days.Sum(item => item.Value) / coveredDays,
            active.OrderByDescending(item => item.Value).FirstOrDefault(),
            weeks.Where(item => item.Total > 0.001).OrderByDescending(item => item.Total).FirstOrDefault());
    }
}

internal static class PlanStatisticsProjection
{
    private const double ActivityEpsilon = 0.001;
    // Anthropic exposes the five-hour and weekly meters independently and publishes no conversion.
    // Observed full-window reports cluster around 7–10 per week, so eight is a visual reference only.
    private const double FullSessionWeeklyReference = 8;
    private static readonly TimeSpan ResetEquivalenceTolerance = TimeSpan.FromMinutes(2);

    internal static ActivityOverview BuildActivity(PlanUsageSeries series, DateTimeOffset now, int? calendarYear = null)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var year = calendarYear ?? today.Year;
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var selectableEnd = year == today.Year && today < yearEnd ? today : yearEnd;
        var calendarStart = WeekStart(yearStart);
        var calendarEnd = WeekStart(yearEnd).AddDays(6);
        var calendarDays = calendarEnd.DayNumber - calendarStart.DayNumber + 1;
        var buckets = Enumerable.Range(0, calendarDays)
            .Select(offset => calendarStart.AddDays(offset))
            .ToDictionary(
                date => date,
                date => new DayAccumulator(date));

        if (series.MetricKind == PlanUsageMetricKind.ActivityValue)
        {
            foreach (var sample in series.Samples
                .Where(item => item.CapturedAt <= now)
                .OrderBy(item => item.CapturedAt))
            {
                var bucket = BucketTime(series, sample);
                var date = DateOnly.FromDateTime(bucket.Date);
                if (date >= yearStart && date <= selectableEnd && buckets.TryGetValue(date, out var day))
                {
                    day.Add(bucket.Hour, sample.UsedPercent);
                }
            }
        }
        else
        {
            foreach (var cycle in GroupByEquivalentReset(series.Samples))
            {
                double? observedPeak = null;
                foreach (var sample in cycle
                    .Where(item => item.CapturedAt <= now)
                    .OrderBy(item => item.CapturedAt))
                {
                    var local = sample.CapturedAt.LocalDateTime;
                    var date = DateOnly.FromDateTime(local.Date);
                    if (date < yearStart || date > selectableEnd || !buckets.TryGetValue(date, out var day))
                    {
                        observedPeak = observedPeak is null
                            ? sample.UsedPercent
                            : Math.Max(observedPeak.Value, sample.UsedPercent);
                        continue;
                    }

                    // The first value is a baseline: attributing it to its capture hour would claim that
                    // all usage since the reset happened at the instant CodexWinBar started observing.
                    // Later activity is counted only when the cycle reaches a new high, so provider
                    // corrections and temporary dips cannot count the same allowance increase twice.
                    var increment = observedPeak is null
                        ? 0
                        : Math.Max(0, sample.UsedPercent - observedPeak.Value);
                    day.Add(local.Hour, increment);
                    observedPeak = observedPeak is null
                        ? sample.UsedPercent
                        : Math.Max(observedPeak.Value, sample.UsedPercent);
                }
            }
        }

        var preliminary = buckets.Values
            .OrderBy(item => item.Date)
            .Select(item => item.Build())
            .ToArray();
        var summaryDays = preliminary
            .Where(day => day.Date >= yearStart && day.Date <= selectableEnd)
            .ToArray();
        var days = preliminary.Select(day => day with
        {
            Intensity = FixedIntensity(series, day.Value),
        }).ToArray();
        var active = days
            .Where(item => item.Date >= yearStart && item.Date <= selectableEnd && item.Value > ActivityEpsilon)
            .ToArray();
        var covered = days
            .Where(item => item.Date >= yearStart && item.Date <= selectableEnd && item.HasCoverage)
            .ToArray();
        var total = summaryDays.Sum(item => item.Value);
        return new ActivityOverview(
            days,
            total,
            active.Length,
            covered.Length,
            covered.Length == 0 ? 0 : total / covered.Length,
            active.OrderByDescending(item => item.Value).FirstOrDefault(),
            yearStart,
            selectableEnd);
    }

    internal static DateOnly WeekStart(DateOnly date)
    {
        var daysSinceSunday = (int)date.DayOfWeek;
        return date.AddDays(-daysSinceSunday);
    }

    /// <summary>
    /// The calendar day a sample belongs to. Quota meters are observed on this machine and follow
    /// local time. Provider-supplied activity buckets (OpenAI daily costs) are UTC days: converting
    /// their midnight start to local time would file each day's spend under the previous local date
    /// for every zone west of UTC.
    /// </summary>
    internal static DateOnly BucketDate(PlanUsageSeries series, PlanUsageSample sample) =>
        DateOnly.FromDateTime(BucketTime(series, sample).Date);

    private static DateTime BucketTime(PlanUsageSeries series, PlanUsageSample sample) =>
        series.MetricKind == PlanUsageMetricKind.ActivityValue
            ? sample.CapturedAt.UtcDateTime
            : sample.CapturedAt.LocalDateTime;

    private static IReadOnlyList<IReadOnlyList<PlanUsageSample>> GroupByEquivalentReset(
        IReadOnlyList<PlanUsageSample> samples)
    {
        var groups = new List<List<PlanUsageSample>>();
        foreach (var sample in samples
            .Where(item => item.ResetsAt is not null)
            .OrderBy(item => item.ResetsAt))
        {
            var existing = groups.LastOrDefault(group =>
                Math.Abs((group[0].ResetsAt!.Value - sample.ResetsAt!.Value).TotalSeconds) <
                ResetEquivalenceTolerance.TotalSeconds);
            if (existing is null)
            {
                groups.Add([sample]);
            }
            else
            {
                existing.Add(sample);
            }
        }

        List<PlanUsageSample>? resetless = null;
        foreach (var sample in samples
            .Where(item => item.ResetsAt is null)
            .OrderBy(item => item.CapturedAt))
        {
            // Providers such as OpenRouter can expose a valid percentage without a reset instant.
            // A material drop is the only observable reset boundary, so begin a new local cycle.
            if (resetless is null || resetless[^1].UsedPercent - sample.UsedPercent > ActivityEpsilon)
            {
                resetless = [sample];
                groups.Add(resetless);
            }
            else
            {
                resetless.Add(sample);
            }
        }

        return groups;
    }

    internal static double FixedIntensity(PlanUsageSeries series, double value)
    {
        if (value <= ActivityEpsilon)
        {
            return 0;
        }

        var maximum = series.MetricKind == PlanUsageMetricKind.ActivityValue
            ? series.ScaleMaximum is > ActivityEpsilon ? series.ScaleMaximum.Value : 100
            : series.WindowMinutes == 300
                ? FullSessionWeeklyReference * 100
                : 100;
        return Math.Clamp(value / maximum, 0, 1);
    }

    private sealed class DayAccumulator(DateOnly date)
    {
        private readonly double[] hourlyValues = new double[24];
        private readonly int[] hourlyObservations = new int[24];

        internal DateOnly Date { get; } = date;

        internal void Add(int hour, double increment)
        {
            this.hourlyValues[hour] += increment;
            this.hourlyObservations[hour]++;
        }

        internal ActivityDay Build()
        {
            var hours = Enumerable.Range(0, 24)
                .Select(hour => new ActivityHour(hour, this.hourlyValues[hour], this.hourlyObservations[hour]))
                .ToArray();
            return new ActivityDay(
                this.Date,
                this.hourlyValues.Sum(),
                this.hourlyValues.Count(value => value > ActivityEpsilon),
                this.hourlyObservations.Sum(),
                hours,
                Intensity: 0);
        }
    }
}
