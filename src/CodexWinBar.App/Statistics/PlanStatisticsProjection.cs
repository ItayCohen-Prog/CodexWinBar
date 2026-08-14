using CodexWinBar.Core.Statistics;

namespace CodexWinBar.App.Statistics;

internal enum ActivityScaleMode
{
    Personal,
    Fixed,
}

internal sealed record ActivityHour(int Hour, double Value, int ObservationCount);

internal sealed record ActivityDay(
    DateOnly Date,
    double Value,
    int ActiveHours,
    int ObservationCount,
    IReadOnlyList<ActivityHour> Hours,
    int PersonalIntensity,
    int FixedIntensity)
{
    internal bool HasCoverage => this.ObservationCount > 0;

    internal int Intensity(ActivityScaleMode mode) => mode == ActivityScaleMode.Personal
        ? this.PersonalIntensity
        : this.FixedIntensity;
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
    private const int CalendarWeeks = 52;
    private const double ActivityEpsilon = 0.001;
    private static readonly TimeSpan ResetEquivalenceTolerance = TimeSpan.FromMinutes(2);

    internal static ActivityOverview BuildActivity(PlanUsageSeries series, DateTimeOffset now)
    {
        var end = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var calendarStart = WeekStart(end).AddDays(-7 * (CalendarWeeks - 1));
        var buckets = Enumerable.Range(0, CalendarWeeks * 7)
            .Select(offset => calendarStart.AddDays(offset))
            .ToDictionary(
                date => date,
                date => new DayAccumulator(date));

        foreach (var cycle in GroupByEquivalentReset(series.Samples))
        {
            if (series.WindowMinutes == 300)
            {
                AddSessionCycle(buckets, cycle, now);
                continue;
            }

            double? observedPeak = null;
            foreach (var sample in cycle
                .Where(item => item.CapturedAt <= now)
                .OrderBy(item => item.CapturedAt))
            {
                var local = sample.CapturedAt.LocalDateTime;
                var date = DateOnly.FromDateTime(local.Date);
                if (!buckets.TryGetValue(date, out var day))
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

        var preliminary = buckets.Values
            .OrderBy(item => item.Date)
            .Select(item => item.Build())
            .ToArray();
        var activeValues = preliminary
            .Where(item => item.Value > ActivityEpsilon)
            .Select(item => item.Value)
            .OrderBy(value => value)
            .ToArray();
        var personalThresholds = PersonalThresholds(activeValues);
        var days = preliminary.Select(day => day with
        {
            PersonalIntensity = Intensity(day.Value, personalThresholds),
            FixedIntensity = FixedIntensity(day.Value),
        }).ToArray();
        var active = days.Where(item => item.Value > ActivityEpsilon).ToArray();
        var covered = days.Where(item => item.HasCoverage).ToArray();
        return new ActivityOverview(
            days,
            days.Sum(item => item.Value),
            active.Length,
            covered.Length,
            covered.Length == 0 ? 0 : days.Sum(item => item.Value) / covered.Length,
            active.OrderByDescending(item => item.Value).FirstOrDefault(),
            calendarStart,
            end);
    }

    private static void AddSessionCycle(
        IReadOnlyDictionary<DateOnly, DayAccumulator> buckets,
        IReadOnlyList<PlanUsageSample> cycle,
        DateTimeOffset now)
    {
        var observed = cycle
            .Where(sample => sample.CapturedAt <= now)
            .OrderBy(sample => sample.CapturedAt)
            .ToArray();
        if (observed.Length == 0)
        {
            return;
        }

        var resetsAt = observed[0].ResetsAt;
        var completed = resetsAt is { } reset && reset <= now;
        var attributionTime = completed
            ? resetsAt!.Value.AddTicks(-1)
            : observed[^1].CapturedAt;
        var local = attributionTime.LocalDateTime;
        var date = DateOnly.FromDateTime(local.Date);
        if (!buckets.TryGetValue(date, out var day))
        {
            return;
        }

        day.Add(local.Hour, observed.Max(sample => sample.UsedPercent), observed.Length);
    }

    internal static DateOnly WeekStart(DateOnly date)
    {
        var daysSinceSunday = (int)date.DayOfWeek;
        return date.AddDays(-daysSinceSunday);
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

        return groups;
    }

    private static double[] PersonalThresholds(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return [0, 0, 0];
        }

        return [Percentile(sorted, 0.25), Percentile(sorted, 0.50), Percentile(sorted, 0.75)];
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var position = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    private static int Intensity(double value, IReadOnlyList<double> thresholds)
    {
        if (value <= ActivityEpsilon)
        {
            return 0;
        }

        if (value <= thresholds[0])
        {
            return 1;
        }

        if (value <= thresholds[1])
        {
            return 2;
        }

        return value <= thresholds[2] ? 3 : 4;
    }

    private static int FixedIntensity(double value) => value switch
    {
        <= ActivityEpsilon => 0,
        <= 5 => 1,
        <= 15 => 2,
        <= 30 => 3,
        _ => 4,
    };

    private sealed class DayAccumulator(DateOnly date)
    {
        private readonly double[] hourlyValues = new double[24];
        private readonly int[] hourlyObservations = new int[24];

        internal DateOnly Date { get; } = date;

        internal void Add(int hour, double increment, int observations = 1)
        {
            this.hourlyValues[hour] += increment;
            this.hourlyObservations[hour] += observations;
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
                PersonalIntensity: 0,
                FixedIntensity: 0);
        }
    }
}
