using CodexWinBar.App.Statistics;
using CodexWinBar.App.Dev;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class PlanStatisticsProjectionTests
{
    [Fact]
    public void BuildActivity_attributes_positive_cycle_deltas_to_local_hours()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var series = Series(
            Sample(now.AddHours(-3), 10, reset),
            Sample(now.AddHours(-2), 26, reset),
            Sample(now.AddHours(-1), 21, reset),
            Sample(now, 35, reset));

        var activity = PlanStatisticsProjection.BuildActivity(series, now);
        var day = Assert.IsType<ActivityDay>(activity.Day(DateOnly.FromDateTime(now.LocalDateTime)));

        Assert.Equal(25, day.Value);
        Assert.Equal(2, day.ActiveHours);
        Assert.Equal(4, day.ObservationCount);
        Assert.Equal(16, day.Hours[now.AddHours(-2).LocalDateTime.Hour].Value);
        Assert.Equal(0, day.Hours[now.AddHours(-1).LocalDateTime.Hour].Value);
        Assert.Equal(9, day.Hours[now.LocalDateTime.Hour].Value);
    }

    [Fact]
    public void BuildActivity_treats_new_reset_as_new_cycle()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var series = Series(
            Sample(now.AddHours(-3), 82, now.AddHours(-2)),
            Sample(now.AddHours(-2), 90, now.AddHours(-2)),
            Sample(now.AddHours(-1), 7, now.AddHours(4)),
            Sample(now, 20, now.AddHours(4)));

        var day = Assert.IsType<ActivityDay>(PlanStatisticsProjection.BuildActivity(series, now)
            .Day(DateOnly.FromDateTime(now.LocalDateTime)));

        Assert.Equal(21, day.Value);
        Assert.Equal(2, day.ActiveHours);
    }

    [Fact]
    public void BuildActivity_detects_observable_resets_for_resetless_provider_meters()
    {
        var now = new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
        var series = Series(
            ResetlessSample(now.AddHours(-4), 20),
            ResetlessSample(now.AddHours(-3), 45),
            ResetlessSample(now.AddHours(-2), 4),
            ResetlessSample(now.AddHours(-1), 19));

        var day = Assert.IsType<ActivityDay>(PlanStatisticsProjection.BuildActivity(series, now)
            .Day(DateOnly.FromDateTime(now.LocalDateTime)));

        Assert.Equal(40, day.Value);
        Assert.Equal(4, day.ObservationCount);
    }

    [Fact]
    public void BuildActivity_adds_provider_supplied_activity_values_directly()
    {
        var now = new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
        var series = new PlanUsageSeries
        {
            Id = "history:api-spend",
            Title = "API spend",
            WindowMinutes = 1440,
            MetricKind = PlanUsageMetricKind.ActivityValue,
            Unit = "USD",
            ScaleMaximum = 100,
            Samples = [ResetlessSample(now.AddDays(-1), 12.5), ResetlessSample(now, 8.25)],
        };

        var activity = PlanStatisticsProjection.BuildActivity(series, now);

        Assert.Equal(20.75, activity.Total);
        Assert.Equal(8.25, activity.Day(DateOnly.FromDateTime(now.UtcDateTime))?.Value);
        Assert.Equal(0.0825, PlanStatisticsProjection.FixedIntensity(series, 8.25), 4);
    }

    [Fact]
    public void BuildActivity_files_provider_activity_buckets_under_their_utc_day()
    {
        // Pick an instant whose UTC date differs from this machine's local date, so the test fails
        // in either direction if buckets are ever converted to local time again.
        var bucketDay = new DateOnly(2026, 8, 12);
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc));
        var bucketStart = offset > TimeSpan.Zero
            ? new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 8, 12, 0, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
        var series = new PlanUsageSeries
        {
            Id = "history:api-spend",
            Title = "API spend",
            WindowMinutes = 1440,
            MetricKind = PlanUsageMetricKind.ActivityValue,
            Unit = "USD",
            ScaleMaximum = 100,
            Samples = [ResetlessSample(bucketStart, 12.5)],
        };

        var activity = PlanStatisticsProjection.BuildActivity(series, now);

        Assert.Equal(bucketDay, PlanStatisticsProjection.BucketDate(series, series.Samples[0]));
        Assert.Equal(12.5, activity.Day(bucketDay)?.Value);
        Assert.Equal(12.5, activity.Total);
        Assert.Equal(12.5, activity.Day(bucketDay)?.Hours[bucketStart.UtcDateTime.Hour].Value);
    }

    [Fact]
    public void Quota_meter_samples_keep_following_local_time()
    {
        var series = Series();
        var sample = Sample(new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.Zero), 10, new DateTimeOffset(2026, 8, 13, 5, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            DateOnly.FromDateTime(sample.CapturedAt.LocalDateTime),
            PlanStatisticsProjection.BucketDate(series, sample));
    }

    [Fact]
    public void BuildActivity_groups_reset_times_across_two_minute_boundary()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = new DateTimeOffset(2026, 8, 13, 17, 1, 59, TimeSpan.Zero);
        var series = Series(
            Sample(now.AddMinutes(-10), 20, reset),
            Sample(now, 44, reset.AddSeconds(2)));

        var day = Assert.IsType<ActivityDay>(PlanStatisticsProjection.BuildActivity(series, now)
            .Day(DateOnly.FromDateTime(now.LocalDateTime)));

        Assert.Equal(24, day.Value);
        Assert.Equal(2, day.ObservationCount);
    }

    [Fact]
    public void BuildActivity_uses_fixed_weekly_and_session_reference_scales()
    {
        var weeklyValues = new[] { 0d, 25d, 50d, 75d, 100d, 125d };
        var sessionValues = new[] { 0d, 200d, 400d, 600d, 800d, 1000d };

        Assert.Equal(
            [0, 0.25, 0.5, 0.75, 1, 1],
            weeklyValues.Select(value => PlanStatisticsProjection.FixedIntensity(Series(), value)));
        Assert.Equal(
            [0, 0.25, 0.5, 0.75, 1, 1],
            sessionValues.Select(value => PlanStatisticsProjection.FixedIntensity(SessionSeries(), value)));
    }

    [Fact]
    public void WeekContaining_returns_sunday_week_and_summary()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var series = Series(
            Sample(now.AddDays(-3).AddMinutes(-1), 0, now.AddDays(-3).AddHours(5)),
            Sample(now.AddDays(-3), 20, now.AddDays(-3).AddHours(5)),
            Sample(now.AddMinutes(-1), 0, now.AddHours(5)),
            Sample(now, 30, now.AddHours(5)));
        var activity = PlanStatisticsProjection.BuildActivity(series, now);

        var week = activity.WeekContaining(DateOnly.FromDateTime(now.LocalDateTime));

        Assert.Equal(DayOfWeek.Sunday, week.StartsOn.DayOfWeek);
        Assert.Equal(50, week.Total);
        Assert.Equal(2, week.ActiveDays);
        Assert.Equal(30, week.BusiestDay?.Value);
    }

    [Fact]
    public void BuildActivity_ignores_samples_captured_after_now()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var series = Series(
            Sample(now.AddHours(-2), 0, reset),
            Sample(now.AddHours(-1), 12, reset),
            Sample(now.AddMinutes(1), 70, reset));

        var day = Assert.IsType<ActivityDay>(PlanStatisticsProjection.BuildActivity(series, now)
            .Day(DateOnly.FromDateTime(now.LocalDateTime)));

        Assert.Equal(12, day.Value);
        Assert.Equal(2, day.ObservationCount);
    }

    [Fact]
    public void BuildActivity_does_not_assign_mid_cycle_baseline_to_capture_hour()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(2);
        var series = Series(
            Sample(now.AddHours(-1), 70, reset),
            Sample(now, 78, reset));

        var day = Assert.IsType<ActivityDay>(PlanStatisticsProjection.BuildActivity(series, now)
            .Day(DateOnly.FromDateTime(now.LocalDateTime)));

        Assert.Equal(8, day.Value);
        Assert.Equal(0, day.Hours[now.AddHours(-1).LocalDateTime.Hour].Value);
        Assert.Equal(8, day.Hours[now.LocalDateTime.Hour].Value);
    }

    [Fact]
    public void BuildActivity_session_mode_sums_observed_usage_across_session_cycles()
    {
        var now = new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
        var firstReset = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var secondReset = new DateTimeOffset(2026, 8, 13, 17, 0, 0, TimeSpan.Zero);
        var series = SessionSeries(
            Sample(firstReset.AddHours(-4), 0, firstReset),
            Sample(firstReset.AddMinutes(-10), 100, firstReset),
            Sample(secondReset.AddHours(-4), 0, secondReset),
            Sample(secondReset.AddMinutes(-10), 100, secondReset));

        var day = Assert.IsType<ActivityDay>(PlanStatisticsProjection.BuildActivity(series, now)
            .Day(new DateOnly(2026, 8, 13)));

        Assert.Equal(200, day.Value);
        Assert.Equal(2, day.ActiveHours);
        Assert.Equal(4, day.ObservationCount);
    }

    [Fact]
    public void MonthContaining_clips_week_totals_to_the_selected_month()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var julyReset = new DateTimeOffset(2026, 7, 31, 17, 0, 0, TimeSpan.Zero);
        var augustReset = new DateTimeOffset(2026, 8, 1, 17, 0, 0, TimeSpan.Zero);
        var series = Series(
            Sample(new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero), 0, julyReset),
            Sample(new DateTimeOffset(2026, 7, 31, 11, 0, 0, TimeSpan.Zero), 80, julyReset),
            Sample(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), 0, augustReset),
            Sample(new DateTimeOffset(2026, 8, 1, 11, 0, 0, TimeSpan.Zero), 20, augustReset));

        var month = PlanStatisticsProjection.BuildActivity(series, now)
            .MonthContaining(new DateOnly(2026, 8, 1));

        Assert.Equal(20, month.Total);
        Assert.Equal(20, month.Weeks[0].Total);
        Assert.DoesNotContain(month.Weeks[0].Days, day => day.Date.Month == 7);
    }

    [Fact]
    public void Fake_history_spreads_weekly_and_session_activity_across_a_work_week()
    {
        using var store = new FakePlanStatisticsStore();
        var now = DateTimeOffset.Now;
        var previousWeek = PlanStatisticsProjection.WeekStart(DateOnly.FromDateTime(now.LocalDateTime.Date)).AddDays(-7);
        var workday = previousWeek.AddDays(3);

        foreach (var series in store.Get(ProviderId.Claude).Series.Where(item => item.Id is "session" or "weekly"))
        {
            var activity = PlanStatisticsProjection.BuildActivity(series, now);
            var day = Assert.IsType<ActivityDay>(activity.Day(workday));
            var week = activity.WeekContaining(workday);

            Assert.Equal(8, day.ActiveHours);
            Assert.Equal(5, week.ActiveDays);
        }
    }

    [Fact]
    public void Fake_history_preserves_visible_continuous_intensity_variation()
    {
        using var store = new FakePlanStatisticsStore();
        var now = DateTimeOffset.Now;
        var series = Assert.Single(store.Get(ProviderId.Codex).Series);

        var intensities = PlanStatisticsProjection.BuildActivity(series, now).Days
            .Where(day => day.Value > 0)
            .Select(day => Math.Round(day.Intensity, 3))
            .Distinct()
            .ToArray();

        Assert.True(intensities.Length >= 5);
    }

    [Fact]
    public void Fake_history_populates_every_shipping_provider()
    {
        using var store = new FakePlanStatisticsStore();

        Assert.All(ProviderIds.All, provider => Assert.NotEmpty(store.Get(provider).Series));
        Assert.Equal(
            PlanUsageMetricKind.ActivityValue,
            Assert.Single(store.Get(ProviderId.OpenAIAdmin).Series).MetricKind);
    }

    [Fact]
    public void BuildActivity_uses_the_selected_calendar_year_with_unavailable_padding()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var series = Series(
            Sample(new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero), 10, reset),
            Sample(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), 30, reset));

        var activity = PlanStatisticsProjection.BuildActivity(series, now, 2026);

        Assert.Equal(new DateOnly(2026, 1, 1), activity.StartsOn);
        Assert.Equal(new DateOnly(2026, 8, 13), activity.EndsOn);
        Assert.Equal(DayOfWeek.Sunday, activity.Days[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, activity.Days[^1].Date.DayOfWeek);
        Assert.Equal(20, activity.Day(new DateOnly(2026, 1, 1))?.Value);
        Assert.All(
            activity.Days.Where(day => day.Date.Year != 2026),
            day => Assert.False(day.HasCoverage));
    }

    [Fact]
    public void BuildActivity_shows_a_complete_past_calendar_year()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

        var activity = PlanStatisticsProjection.BuildActivity(Series(), now, 2025);

        Assert.Equal(new DateOnly(2025, 1, 1), activity.StartsOn);
        Assert.Equal(new DateOnly(2025, 12, 31), activity.EndsOn);
    }

    private static PlanUsageSeries Series(params PlanUsageSample[] samples) => new()
    {
        Id = "weekly",
        Title = "Weekly",
        WindowMinutes = 10080,
        Samples = samples,
    };

    private static PlanUsageSeries SessionSeries(params PlanUsageSample[] samples) => new()
    {
        Id = "session",
        Title = "Session",
        WindowMinutes = 300,
        Samples = samples,
    };

    private static PlanUsageSample Sample(DateTimeOffset captured, double used, DateTimeOffset reset) => new()
    {
        CapturedAt = captured,
        UsedPercent = used,
        ResetsAt = reset,
    };

    private static PlanUsageSample ResetlessSample(DateTimeOffset captured, double used) => new()
    {
        CapturedAt = captured,
        UsedPercent = used,
    };
}
