using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexWinBar.App.Assets;
using CodexWinBar.App.Dev;
using CodexWinBar.App.Statistics;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;
using CodexWinBar.Providers;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class StatisticsWindowTests
{
    [Fact]
    public void Calendar_activity_ink_keeps_codex_neutral_and_claude_branded()
    {
        var orange = Color.FromRgb(217, 119, 87);

        Assert.Equal(
            Color.FromRgb(18, 20, 23),
            StatisticsWindow.CalendarActivityInk(ProviderId.Codex, orange, isDark: false));
        Assert.Equal(
            orange,
            StatisticsWindow.CalendarActivityInk(ProviderId.Claude, orange, isDark: false));
        Assert.Equal(
            orange,
            StatisticsWindow.CalendarActivityInk(ProviderId.Claude, orange, isDark: true));
    }

    [Fact]
    public void Near_black_brand_inks_go_light_neutral_only_in_dark_mode()
    {
        var copilot = Color.FromRgb(36, 41, 47);
        var cursor = Color.FromRgb(70, 74, 82);
        var openRouter = Color.FromRgb(101, 82, 255);
        var lightNeutral = Color.FromRgb(238, 240, 244);

        Assert.Equal(copilot, StatisticsWindow.CalendarActivityInk(ProviderId.Copilot, copilot, isDark: false));
        Assert.Equal(cursor, StatisticsWindow.CalendarActivityInk(ProviderId.Cursor, cursor, isDark: false));
        Assert.Equal(lightNeutral, StatisticsWindow.CalendarActivityInk(ProviderId.Copilot, copilot, isDark: true));
        Assert.Equal(lightNeutral, StatisticsWindow.CalendarActivityInk(ProviderId.Cursor, cursor, isDark: true));
        Assert.Equal(openRouter, StatisticsWindow.CalendarActivityInk(ProviderId.OpenRouter, openRouter, isDark: true));
    }

    [Fact]
    public void Limit_names_lose_generic_capitals_but_keep_brand_casing_in_sentences()
    {
        Assert.Equal("weekly limit", StatisticsWindow.LimitNameInSentence(new PlanUsageSeries
        {
            Id = "weekly",
            Title = "Weekly",
            WindowMinutes = 10080,
        }));
        Assert.Equal("5-hour session", StatisticsWindow.LimitNameInSentence(new PlanUsageSeries
        {
            Id = "session",
            Title = "Session",
            WindowMinutes = 300,
        }));
        Assert.Equal("Fable 5 limit", StatisticsWindow.LimitNameInSentence(new PlanUsageSeries
        {
            Id = "extra:claude-weekly-fable",
            Title = "Fable 5",
            WindowMinutes = 10080,
        }));
    }

    [Fact]
    public void Calendar_legend_names_the_selected_series_scale()
    {
        Assert.Equal("Fixed scale · 0–100% of weekly limit", StatisticsWindow.CalendarScaleLegend(new PlanUsageSeries
        {
            Id = "weekly",
            Title = "Weekly",
            WindowMinutes = 10080,
        }));
        Assert.Equal("Fixed scale · 0–100% of Fable 5 limit", StatisticsWindow.CalendarScaleLegend(new PlanUsageSeries
        {
            Id = "extra:claude-weekly-fable",
            Title = "Fable 5",
            WindowMinutes = 10080,
        }));
        Assert.Equal("Fixed scale · 0–8 full sessions (weekly-equivalent reference)", StatisticsWindow.CalendarScaleLegend(new PlanUsageSeries
        {
            Id = "session",
            Title = "Session",
            WindowMinutes = 300,
        }));
        Assert.Equal("Fixed scale · $0.00–$100.00 per day", StatisticsWindow.CalendarScaleLegend(new PlanUsageSeries
        {
            Id = "history:api-spend",
            Title = "API spend",
            WindowMinutes = 1440,
            MetricKind = PlanUsageMetricKind.ActivityValue,
            Unit = "USD",
            ScaleMaximum = 100,
        }));
    }

    [Fact]
    public void Calendar_caption_names_the_period_of_the_selected_series_scale()
    {
        Assert.Equal("Fixed weekly scale", StatisticsWindow.CalendarScaleCaption(new PlanUsageSeries
        {
            Id = "weekly",
            Title = "Weekly",
            WindowMinutes = 10080,
        }));
        Assert.Equal("Fixed weekly scale", StatisticsWindow.CalendarScaleCaption(new PlanUsageSeries
        {
            Id = "session",
            Title = "Session",
            WindowMinutes = 300,
        }));
        Assert.Equal("Fixed monthly scale", StatisticsWindow.CalendarScaleCaption(new PlanUsageSeries
        {
            Id = "premium-interactions",
            Title = "Premium interactions",
            WindowMinutes = 43200,
        }));
        Assert.Equal("Fixed daily scale", StatisticsWindow.CalendarScaleCaption(new PlanUsageSeries
        {
            Id = "pro",
            Title = "Pro",
            WindowMinutes = 1440,
        }));
        Assert.Equal("Fixed scale", StatisticsWindow.CalendarScaleCaption(new PlanUsageSeries
        {
            Id = "key-limit",
            Title = "Key limit",
            WindowMinutes = 0,
        }));
        Assert.Equal("Fixed daily scale", StatisticsWindow.CalendarScaleCaption(new PlanUsageSeries
        {
            Id = "history:api-spend",
            Title = "API spend",
            WindowMinutes = 1440,
            MetricKind = PlanUsageMetricKind.ActivityValue,
            Unit = "USD",
            ScaleMaximum = 100,
        }));
    }

    [Fact]
    public void Week_comparison_matches_the_same_weekdays_and_only_elapsed_days()
    {
        var weekly = new PlanUsageSeries { Id = "weekly", Title = "Weekly", WindowMinutes = 10080 };
        // Previous full week Jul 19–25: 10% on each weekday, 2% on Saturday.
        var previous = Week(
            new DateOnly(2026, 7, 19),
            Day(new DateOnly(2026, 7, 19), 0),
            Day(new DateOnly(2026, 7, 20), 10),
            Day(new DateOnly(2026, 7, 21), 10),
            Day(new DateOnly(2026, 7, 22), 10),
            Day(new DateOnly(2026, 7, 23), 10),
            Day(new DateOnly(2026, 7, 24), 10),
            Day(new DateOnly(2026, 7, 25), 2));

        // August's opening week holds Saturday Aug 1 only: 3% against last Saturday's 2%, never
        // against the previous week's 52% total.
        var opening = Week(new DateOnly(2026, 7, 26), Day(new DateOnly(2026, 8, 1), 3));
        Assert.Equal("+1%", StatisticsWindow.WeekComparison(weekly, opening, previous, new DateOnly(2026, 8, 13)));

        // A week viewed on its Tuesday compares Sunday–Tuesday with the same three days before it.
        var inProgress = Week(
            new DateOnly(2026, 7, 26),
            Day(new DateOnly(2026, 7, 26), 0),
            Day(new DateOnly(2026, 7, 27), 11),
            Day(new DateOnly(2026, 7, 28), 9),
            Day(new DateOnly(2026, 7, 29), 0, covered: false),
            Day(new DateOnly(2026, 7, 30), 0, covered: false),
            Day(new DateOnly(2026, 7, 31), 0, covered: false),
            Day(new DateOnly(2026, 8, 1), 0, covered: false));
        Assert.Equal("About the same", StatisticsWindow.WeekComparison(weekly, inProgress, previous, new DateOnly(2026, 7, 28)));
        Assert.Equal("-32%", StatisticsWindow.WeekComparison(weekly, inProgress, previous, new DateOnly(2026, 8, 13)));

        // Nothing elapsed yet, or no observation on the matching previous days: no comparison.
        Assert.Equal("No comparison", StatisticsWindow.WeekComparison(weekly, inProgress, previous, new DateOnly(2026, 7, 25)));
        var unobserved = Week(
            new DateOnly(2026, 7, 19),
            Day(new DateOnly(2026, 7, 25), 0, covered: false));
        Assert.Equal("No comparison", StatisticsWindow.WeekComparison(weekly, opening, unobserved, new DateOnly(2026, 8, 13)));
    }

    private static ActivityDay Day(DateOnly date, double value, bool covered = true) =>
        new(date, value, value > 0 ? 1 : 0, covered ? 1 : 0, [], 0);

    private static ActivityWeek Week(DateOnly startsOn, params ActivityDay[] days) => new(
        startsOn,
        days,
        days.Sum(day => day.Value),
        days.Count(day => day.Value > 0),
        days.Count(day => day.HasCoverage),
        days.Where(day => day.Value > 0).OrderByDescending(day => day.Value).FirstOrDefault());

    [Fact]
    public void Shipping_provider_activity_colors_are_preselected_and_unique()
    {
        var descriptors = ProviderCatalog.CreateAll();
        var colors = descriptors.Select(descriptor => StatisticsWindow.CalendarActivityInk(
            descriptor.Id,
            Color.FromRgb(descriptor.Branding.R, descriptor.Branding.G, descriptor.Branding.B),
            isDark: false));

        Assert.Equal(descriptors.Count, colors.Distinct().Count());
    }

    [Fact]
    public void Inspection_counts_pluralize_correctly()
    {
        Assert.Equal("1 observation", StatisticsAccessibility.Count(1, "observation"));
        Assert.Equal("0 observations", StatisticsAccessibility.Count(0, "observation"));
        Assert.Equal("8 observations", StatisticsAccessibility.Count(8, "observation"));
        Assert.Equal("1 active hour", StatisticsAccessibility.Count(1, "active hour"));
        Assert.Equal("5 active days", StatisticsAccessibility.Count(5, "active day"));
    }

    [Fact]
    public void Dashboard_gains_a_right_inset_only_while_the_scrollbar_is_visible()
    {
        Assert.Equal(new System.Windows.Thickness(0, 0, 10, 0), StatisticsWindow.DashboardScrollInset(System.Windows.Visibility.Visible));
        Assert.Equal(new System.Windows.Thickness(0), StatisticsWindow.DashboardScrollInset(System.Windows.Visibility.Collapsed));
        Assert.Equal(new System.Windows.Thickness(0), StatisticsWindow.DashboardScrollInset(System.Windows.Visibility.Hidden));
    }

    [Fact]
    public void Week_navigation_anchors_the_opening_week_inside_the_selected_year()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), StatisticsWindow.WeekAnchorWithinYear(new DateOnly(2025, 12, 28), 2026));
        Assert.Equal(new DateOnly(2026, 1, 4), StatisticsWindow.WeekAnchorWithinYear(new DateOnly(2026, 1, 4), 2026));
        Assert.Equal(new DateOnly(2025, 12, 28), StatisticsWindow.WeekAnchorWithinYear(new DateOnly(2025, 12, 28), 2025));
    }

    [Fact]
    public void Initial_inspection_prefers_the_selected_active_period_then_the_busiest_one()
    {
        // An idle Sunday at index 0 must not open the inspector on "0%" when the week had activity.
        var activity = new[] { false, true, true, false };

        Assert.Equal(1, StatisticsWindow.InitialInspectionIndex(1, activity, busiestIndex: 2));
        Assert.Equal(2, StatisticsWindow.InitialInspectionIndex(0, activity, busiestIndex: 2));
        Assert.Equal(0, StatisticsWindow.InitialInspectionIndex(0, activity, busiestIndex: null));
        Assert.Equal(3, StatisticsWindow.InitialInspectionIndex(9, activity, busiestIndex: null));
        Assert.Equal(0, StatisticsWindow.InitialInspectionIndex(0, [], busiestIndex: null));
    }

    [Fact]
    public void Time_basis_names_utc_days_only_for_provider_activity_buckets()
    {
        Assert.Equal("Local time", StatisticsWindow.TimeBasis(new PlanUsageSeries
        {
            Id = "weekly",
            Title = "Weekly",
            WindowMinutes = 10080,
        }));
        Assert.Equal("UTC days", StatisticsWindow.TimeBasis(new PlanUsageSeries
        {
            Id = "history:api-spend",
            Title = "API spend",
            WindowMinutes = 1440,
            MetricKind = PlanUsageMetricKind.ActivityValue,
            Unit = "USD",
        }));
    }

    [Fact]
    public void Rail_switches_to_white_variants_only_for_near_black_marks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(LogoImages.IsDarkMark("copilot"));
                Assert.True(LogoImages.IsDarkMark("cursor"));
                Assert.False(LogoImages.IsDarkMark("codex"));
                Assert.False(LogoImages.IsDarkMark("claude"));
                Assert.False(LogoImages.IsDarkMark("gemini"));
                Assert.False(LogoImages.IsDarkMark("openrouter"));
                Assert.False(LogoImages.IsDarkMark("zai"));
                Assert.False(LogoImages.IsDarkMark("no-such-logo"));

                // Every near-black shipping mark has a white variant to fall back on.
                foreach (var key in new[] { "copilot", "cursor" })
                {
                    Assert.NotEqual(
                        ProviderAssets.GetLogoPng(key, darkBackground: false),
                        ProviderAssets.GetLogoPng(key, darkBackground: true));
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Logo luminance check did not finish.");
        Assert.Null(failure);
    }

    [Fact]
    public void Refresh_can_rebuild_dashboard_repeatedly_without_visual_reparenting()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var store = new FakePlanStatisticsStore();
                var constructor = typeof(StatisticsWindow).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [typeof(IPlanStatisticsStore), typeof(IReadOnlyList<ProviderDescriptor>)],
                    modifiers: null);
                var window = Assert.IsType<StatisticsWindow>(constructor?.Invoke([store, ProviderCatalog.CreateAll()]));
                var refresh = typeof(StatisticsWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);
                var selectDate = typeof(StatisticsWindow).GetMethod("SelectDate", BindingFlags.Instance | BindingFlags.NonPublic);
                var selectMonth = typeof(StatisticsWindow).GetMethod("SelectMonth", BindingFlags.Instance | BindingFlags.NonPublic);
                var selectWeek = typeof(StatisticsWindow).GetMethod("SelectWeek", BindingFlags.Instance | BindingFlags.NonPublic);
                var goBack = typeof(StatisticsWindow).GetMethod("GoBack", BindingFlags.Instance | BindingFlags.NonPublic);
                var setProvider = typeof(StatisticsWindow).GetMethod("SetProviderSelection", BindingFlags.Instance | BindingFlags.NonPublic);
                var selectedDate = typeof(StatisticsWindow).GetField("selectedDate", BindingFlags.Instance | BindingFlags.NonPublic);
                var viewMode = typeof(StatisticsWindow).GetField("viewMode", BindingFlags.Instance | BindingFlags.NonPublic);
                var providerTabs = typeof(StatisticsWindow).GetField("providerTabs", BindingFlags.Instance | BindingFlags.NonPublic);

                refresh?.Invoke(window, null);
                var tabs = Assert.IsType<System.Windows.Controls.StackPanel>(providerTabs?.GetValue(window));
                Assert.Equal(ProviderCatalog.CreateAll().Count, tabs.Children.Count);
                var firstProviderButton = tabs.Children[0];
                foreach (var provider in ProviderCatalog.CreateAll().Select(descriptor => descriptor.Id))
                {
                    setProvider?.Invoke(window, [provider]);
                    refresh?.Invoke(window, null);
                }

                Assert.Same(firstProviderButton, tabs.Children[0]);

                Assert.Equal(ActivityViewMode.Overview, viewMode?.GetValue(window));
                selectMonth?.Invoke(window, [DateOnly.FromDateTime(DateTime.Today)]);
                Assert.Equal(ActivityViewMode.Month, viewMode?.GetValue(window));
                selectWeek?.Invoke(window, [PlanStatisticsProjection.WeekStart(DateOnly.FromDateTime(DateTime.Today))]);
                Assert.Equal(ActivityViewMode.Week, viewMode?.GetValue(window));
                selectDate?.Invoke(window, [DateOnly.FromDateTime(DateTime.Today.AddDays(-2))]);
                Assert.Equal(ActivityViewMode.Day, viewMode?.GetValue(window));
                var dateBeforeProviderChange = selectedDate?.GetValue(window);
                setProvider?.Invoke(window, [ProviderId.Claude]);
                Assert.Equal(ActivityViewMode.Day, viewMode?.GetValue(window));
                Assert.Equal(dateBeforeProviderChange, selectedDate?.GetValue(window));
                goBack?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Week, viewMode?.GetValue(window));
                goBack?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Month, viewMode?.GetValue(window));
                goBack?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Overview, viewMode?.GetValue(window));
                refresh?.Invoke(window, null);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF dashboard refresh did not finish.");
        Assert.Null(failure);
    }

    [Fact]
    public void Previous_week_from_the_opening_week_stays_in_january_instead_of_snapping_to_the_latest_week()
    {
        RunOnStaThread(() =>
        {
            using var store = new FakePlanStatisticsStore();
            var window = new WindowHarness(store);
            var year = DateTime.Today.Year;
            window.Invoke("SelectMonth", new DateOnly(year, 1, 5));
            window.Invoke("SelectWeek", PlanStatisticsProjection.WeekStart(new DateOnly(year, 1, 5)));
            Assert.Equal(ActivityViewMode.Week, window.Field<ActivityViewMode>("viewMode"));

            // The week before Jan 4-10 starts in the previous December.
            var openingWeek = PlanStatisticsProjection.WeekStart(new DateOnly(year, 1, 1));
            window.Invoke("NavigateWeek", openingWeek);

            Assert.Equal(ActivityViewMode.Week, window.Field<ActivityViewMode>("viewMode"));
            Assert.Equal(new DateOnly(year, 1, 1), window.Field<DateOnly?>("selectedDate"));
            Assert.Equal(new DateOnly(year, 1, 1), window.Field<DateOnly?>("selectedMonth"));
            Assert.Equal(year, window.Field<int>("selectedYear"));
        });
    }

    [Fact]
    public void Drilldown_navigation_buttons_carry_accessible_names()
    {
        RunOnStaThread(() =>
        {
            using var store = new FakePlanStatisticsStore();
            var window = new WindowHarness(store);
            var today = DateOnly.FromDateTime(DateTime.Today);

            window.Invoke("SelectMonth", today);
            Assert.Contains(window.ButtonNames(), name => name == "Previous month");
            Assert.Contains(window.ButtonNames(), name => name == "Next month");

            window.Invoke("SelectWeek", PlanStatisticsProjection.WeekStart(today));
            Assert.Contains(window.ButtonNames(), name => name == "Previous week");
            Assert.Contains(window.ButtonNames(), name => name == "Next week");

            window.Invoke("SelectDate", today);
            Assert.Contains(window.ButtonNames(), name => name == "Previous day");
            Assert.Contains(window.ButtonNames(), name => name == "Next day");
        });
    }

    [Fact]
    public void Store_changes_rebuild_the_dashboard_once_the_user_is_not_interacting()
    {
        RunOnStaThread(() =>
        {
            using var store = new NotifyingStore();
            var window = new WindowHarness(store);
            var host = window.Field<ContentControl>("dashboardHost");
            var before = host.Content;

            store.RaiseStateChanged();
            store.RaiseStateChanged();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.NotSame(before, host.Content);
            Assert.False(window.Field<bool>("refreshDeferred"));
        });
    }

    private static void RunOnStaThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF statistics window test did not finish.");
        Assert.Null(failure);
    }

    private sealed class NotifyingStore : IPlanStatisticsStore
    {
        private readonly FakePlanStatisticsStore inner = new();

        public event Action? StateChanged;

        public ProviderPlanStatistics Get(ProviderId provider) => this.inner.Get(provider);

        public void Record(CodexWinBar.Core.Models.UsageSnapshot snapshot)
        {
        }

        public void RaiseStateChanged() => this.StateChanged?.Invoke();

        public void Dispose() => this.inner.Dispose();
    }

    private sealed class WindowHarness
    {
        private readonly StatisticsWindow window;

        public WindowHarness(IPlanStatisticsStore store)
        {
            var constructor = typeof(StatisticsWindow).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(IPlanStatisticsStore), typeof(IReadOnlyList<ProviderDescriptor>)],
                modifiers: null);
            this.window = Assert.IsType<StatisticsWindow>(constructor?.Invoke([store, ProviderCatalog.CreateAll()]));
        }

        public void Invoke(string method, params object[] arguments)
        {
            var target = typeof(StatisticsWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(target);
            target.Invoke(this.window, arguments);
        }

        public T Field<T>(string name)
        {
            var field = typeof(StatisticsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field.GetValue(this.window)!;
        }

        public IReadOnlyList<string> ButtonNames()
        {
            var names = new List<string>();
            Collect(this.Field<ContentControl>("dashboardHost"), names);
            return names;
        }

        private static void Collect(DependencyObject element, List<string> names)
        {
            if (element is Button button)
            {
                names.Add(AutomationProperties.GetName(button));
            }

            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            {
                Collect(child, names);
            }
        }
    }
}
