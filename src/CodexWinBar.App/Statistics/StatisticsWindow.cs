using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using CodexWinBar.App.Assets;
using CodexWinBar.App.Interop;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;
using Microsoft.Win32;

namespace CodexWinBar.App.Statistics;

internal enum ActivityViewMode
{
    Overview,
    Month,
    Week,
    Day,
}

/// <summary>Locally observed provider activity with calendar, weekly, and hourly drill-downs.</summary>
public sealed class StatisticsWindow : Window
{
    private const string BackGlyph = "\uE72B";
    private const string CalendarGlyph = "\uE787";
    private const string ClockGlyph = "\uE823";
    private const string HistoryGlyph = "\uE81C";
    private const string ScaleGlyph = "\uE8AB";
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly CultureInfo UiCulture = CultureInfo.GetCultureInfo("en-US");
    private static StatisticsWindow? current;
    private readonly IPlanStatisticsStore store;
    private readonly IReadOnlyDictionary<ProviderId, ProviderDescriptor> descriptors;
    private readonly StackPanel providerTabs = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl dashboardHost = new();
    private readonly bool isDark;
    private ProviderId selectedProvider = ProviderId.Codex;
    private string? selectedSeriesId;
    private DateOnly? selectedDate;
    private DateOnly? selectedMonth;
    private ActivityScaleMode scaleMode = ActivityScaleMode.Personal;
    private ActivityViewMode viewMode = ActivityViewMode.Overview;
    private bool providerTransitioning;

    private StatisticsWindow(
        IPlanStatisticsStore store,
        IReadOnlyList<ProviderDescriptor> descriptors)
    {
        this.store = store;
        this.descriptors = descriptors.ToDictionary(item => item.Id);
        this.isDark = !SystemAppsUseLightTheme();

        this.Title = "CodexWinBar Statistics";
        this.Width = 1040;
        this.Height = 820;
        this.MinWidth = 900;
        this.MinHeight = 650;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.WindowStyle = WindowStyle.SingleBorderWindow;
        this.ShowInTaskbar = true;
        this.UseLayoutRounding = true;
        this.SnapsToDevicePixels = true;
        this.FontFamily = TextFont;
        this.Resources.MergedDictionaries.Add(StatisticsTheme.Create(this.isDark));
        this.Foreground = this.Brush("StatisticsForeground");
        this.Background = this.Brush("StatisticsWindowBackground");
        this.Content = this.BuildRoot();

        this.SourceInitialized += (_, _) => WpfDwm.ApplyStandardWindowChrome(this, this.isDark);
        this.ContentRendered += (_, _) => WpfDwm.EnsureTitleBarVisible(this);
        this.PreviewKeyDown += (_, e) =>
        {
            if (this.viewMode != ActivityViewMode.Overview &&
                e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                this.GoBack();
                e.Handled = true;
            }
        };
        this.store.StateChanged += this.OnStatisticsChanged;
        this.Closed += (_, _) =>
        {
            this.store.StateChanged -= this.OnStatisticsChanged;
            if (ReferenceEquals(current, this))
            {
                current = null;
            }
        };
        this.Refresh();
    }

    /// <summary>Shows the singleton statistics window, or activates the existing instance.</summary>
    internal static void ShowOrActivate(
        IPlanStatisticsStore store,
        IReadOnlyList<ProviderDescriptor> descriptors)
    {
        if (current is { IsVisible: true })
        {
            if (current.WindowState == WindowState.Minimized)
            {
                current.WindowState = WindowState.Normal;
            }

            current.Activate();
            return;
        }

        current = new StatisticsWindow(store, descriptors);
        current.Show();
        current.Activate();
    }

    private Grid BuildRoot()
    {
        var root = new Grid { Margin = new Thickness(32, 26, 32, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var titleIcon = LogoImages.IconGlyph(LogoImages.StatisticsGlyph, 22);
        titleIcon.Margin = new Thickness(0, 2, 10, 0);
        titleIcon.Foreground = this.Brush("StatisticsForeground");
        titleRow.Children.Add(titleIcon);
        titleRow.Children.Add(new TextBlock
        {
            Text = "Activity",
            FontFamily = DisplayFont,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
        });
        var heading = new StackPanel();
        heading.Children.Add(titleRow);
        heading.Children.Add(new TextBlock
        {
            Text = "Your observed AI usage, from the month down to the hour",
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 13,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        header.Children.Add(heading);
        var localHistory = this.IconLabel(HistoryGlyph, "Local history", iconSize: 13);
        localHistory.VerticalAlignment = VerticalAlignment.Top;
        localHistory.Margin = new Thickness(0, 8, 0, 0);
        localHistory.ToolTip = "Built from successful local provider refreshes. Quota activity is observed locally and never estimated from token counts.";
        Grid.SetColumn(localHistory, 1);
        header.Children.Add(localHistory);
        root.Children.Add(header);

        var providers = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = this.providerTabs,
            Height = 48,
            Margin = new Thickness(0, 0, 0, 18),
        };
        Grid.SetRow(providers, 1);
        root.Children.Add(providers);

        this.dashboardHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        this.dashboardHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = this.dashboardHost,
        };
        Grid.SetRow(scroller, 2);
        root.Children.Add(scroller);
        return root;
    }

    private void Refresh()
    {
        this.RefreshProviderTabs();
        var statistics = this.store.Get(this.selectedProvider);
        var series = statistics.Series.FirstOrDefault(item => item.Id == this.selectedSeriesId)
            ?? statistics.Series.FirstOrDefault();
        this.selectedSeriesId = series?.Id;
        if (series is null)
        {
            this.dashboardHost.Content = this.BuildEmptyState();
            return;
        }

        var activity = PlanStatisticsProjection.BuildActivity(series, DateTimeOffset.Now);
        var latestCovered = activity.Days.LastOrDefault(day => day.HasCoverage)?.Date ?? activity.EndsOn;
        var earliestMonth = new DateOnly(activity.StartsOn.Year, activity.StartsOn.Month, 1);
        var latestMonth = new DateOnly(activity.EndsOn.Year, activity.EndsOn.Month, 1);
        if (this.selectedMonth is null || this.selectedMonth.Value < earliestMonth || this.selectedMonth.Value > latestMonth)
        {
            this.selectedMonth = new DateOnly(latestCovered.Year, latestCovered.Month, 1);
        }

        if (this.selectedDate is null || activity.Day(this.selectedDate.Value) is null)
        {
            this.selectedDate = latestCovered;
        }

        this.dashboardHost.Content = this.BuildDashboard(series, statistics.Series, activity);
    }

    private void RefreshProviderTabs()
    {
        this.providerTabs.Children.Clear();
        var providerIds = this.descriptors.Keys
            .Where(id => id is ProviderId.Codex or ProviderId.Claude || this.store.Get(id).Series.Count > 0)
            .OrderBy(ProviderOrder)
            .ThenBy(id => this.descriptors[id].Metadata.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!providerIds.Contains(this.selectedProvider))
        {
            this.selectedProvider = providerIds.FirstOrDefault(ProviderId.Codex);
        }

        foreach (var provider in providerIds)
        {
            var descriptor = this.descriptors[provider];
            var selected = provider == this.selectedProvider;
            var button = this.CreateProviderButton(descriptor, selected);
            button.Click += (_, _) => this.SelectProvider(provider, button);
            AutomationProperties.SetName(button, $"{descriptor.Metadata.DisplayName} activity");
            this.providerTabs.Children.Add(button);
        }
    }

    private Button CreateProviderButton(ProviderDescriptor descriptor, bool selected)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FrameworkElement logo = LogoImages.Get(descriptor.Branding.GlyphKey, this.isDark) is { } source
            ? new Image { Source = source, Width = 27, Height = 27 }
            : LogoImages.IconGlyph(LogoImages.StatisticsGlyph, 24);
        var shadow = new DropShadowEffect
        {
            BlurRadius = selected ? 12 : 9,
            Direction = 270,
            ShadowDepth = selected ? 3 : 2,
            Opacity = selected ? 0.38 : 0.24,
            Color = this.isDark ? Colors.Black : Color.FromRgb(50, 55, 66),
        };
        logo.Effect = shadow;
        logo.Opacity = selected ? 1 : 0.38;
        logo.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        logo.RenderTransform = scale;
        content.Children.Add(logo);

        var name = new TextBlock
        {
            Text = descriptor.Metadata.DisplayName,
            Margin = new Thickness(10, 0, 1, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var nameWidth = name.DesiredSize.Width;
        var nameHost = new Border
        {
            Width = selected ? double.NaN : 0,
            Opacity = selected ? 1 : 0,
            ClipToBounds = true,
            Child = name,
        };
        content.Children.Add(nameHost);

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(5, 5, selected ? 7 : 5, 7),
            Margin = new Thickness(0, 0, 14, 0),
            MinWidth = 37,
            MinHeight = 39,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = descriptor.Metadata.DisplayName,
            Template = CreateProviderButtonTemplate(),
        };
        button.Tag = new ProviderButtonVisual(descriptor.Id, logo, nameHost, nameWidth, shadow);
        ToolTipService.SetInitialShowDelay(button, 250);
        ToolTipService.SetBetweenShowDelay(button, 80);
        void HighlightLogo()
        {
            logo.Opacity = 1;
            scale.ScaleX = 1.08;
            scale.ScaleY = 1.08;
            shadow.BlurRadius = 13;
            shadow.Opacity = 0.42;
            shadow.ShadowDepth = 3;
        }

        void RestoreLogo()
        {
            logo.Opacity = selected ? 1 : 0.38;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            shadow.BlurRadius = selected ? 12 : 9;
            shadow.Opacity = selected ? 0.38 : 0.24;
            shadow.ShadowDepth = selected ? 3 : 2;
        }

        button.MouseEnter += (_, _) => HighlightLogo();
        button.MouseLeave += (_, _) => RestoreLogo();
        button.GotKeyboardFocus += (_, _) => HighlightLogo();
        button.LostKeyboardFocus += (_, _) => RestoreLogo();
        return button;
    }

    private void SelectProvider(ProviderId provider, Button targetButton)
    {
        if (provider == this.selectedProvider || this.providerTransitioning)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation ||
            targetButton.Tag is not ProviderButtonVisual target ||
            this.providerTabs.Children.OfType<Button>()
                .Select(button => button.Tag)
                .OfType<ProviderButtonVisual>()
                .FirstOrDefault(item => item.Provider == this.selectedProvider) is not { } current)
        {
            this.CommitProviderSelection(provider, animateDashboard: false);
            return;
        }

        this.providerTransitioning = true;
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        current.NameHost.Width = current.NameHost.ActualWidth;
        current.NameHost.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(current.NameHost.ActualWidth, 0, duration) { EasingFunction = easing });
        current.NameHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = easing });
        current.Logo.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0.38, duration) { EasingFunction = easing });
        current.Shadow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            new DoubleAnimation(0.38, 0.24, duration) { EasingFunction = easing });

        target.NameHost.Width = 0;
        target.NameHost.Opacity = 0;
        target.NameHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        target.Logo.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(target.Logo.Opacity, 1, duration) { EasingFunction = easing });
        target.Shadow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            new DoubleAnimation(target.Shadow.Opacity, 0.38, duration) { EasingFunction = easing });
        var expand = new DoubleAnimation(0, target.NameWidth, duration) { EasingFunction = easing };
        expand.Completed += (_, _) => this.CommitProviderSelection(provider, animateDashboard: true);
        target.NameHost.BeginAnimation(WidthProperty, expand);
    }

    private void CommitProviderSelection(ProviderId provider, bool animateDashboard)
    {
        this.selectedProvider = provider;
        this.selectedSeriesId = null;
        this.selectedDate = null;
        this.selectedMonth = null;
        this.viewMode = ActivityViewMode.Overview;
        if (animateDashboard)
        {
            this.dashboardHost.Opacity = 0.55;
        }

        this.Refresh();
        if (animateDashboard)
        {
            this.dashboardHost.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }

        this.providerTransitioning = false;
    }

    private UIElement BuildDashboard(
        PlanUsageSeries series,
        IReadOnlyList<PlanUsageSeries> allSeries,
        ActivityOverview activity)
    {
        var descriptor = this.descriptors[this.selectedProvider];
        var rawAccent = Color.FromRgb(descriptor.Branding.R, descriptor.Branding.G, descriptor.Branding.B);
        var accent = this.isDark ? Blend(rawAccent, Colors.White, 0.32) : rawAccent;
        var selected = activity.Day(this.selectedDate!.Value) ?? activity.Days[^1];
        var week = activity.WeekContaining(selected.Date);
        var month = activity.MonthContaining(this.selectedMonth!.Value);
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(this.BuildControlRow(allSeries));
        root.Children.Add(this.BuildTimeframeHeader(activity, selected, week, month));
        switch (this.viewMode)
        {
            case ActivityViewMode.Overview:
                root.Children.Add(this.BuildGeneralSummary(descriptor, activity, accent));
                root.Children.Add(this.BuildOverviewSection(activity, selected, accent));
                break;
            case ActivityViewMode.Month:
                root.Children.Add(this.BuildMonthSummary(descriptor, month, accent));
                root.Children.Add(this.BuildMonthSection(month, accent));
                break;
            case ActivityViewMode.Week:
                root.Children.Add(this.BuildWeekSummary(descriptor, activity, week, accent));
                root.Children.Add(this.BuildWeekSection(activity, week, accent));
                break;
            case ActivityViewMode.Day:
                root.Children.Add(this.BuildDaySummary(descriptor, activity, selected, accent));
                root.Children.Add(this.BuildDaySection(selected, accent));
                break;
        }

        return root;
    }

    private UIElement BuildControlRow(IReadOnlyList<PlanUsageSeries> series)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16), MinHeight = 34 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(this.BuildSeriesTabs(series));

        var metrics = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        metrics.Children.Add(this.CreateTabButton(this.IconLabel(LogoImages.StatisticsGlyph, "Quota", true), selected: true, compact: true));
        var tokens = this.CreateTabButton(this.IconLabel("\uE8B7", "Tokens"), selected: false, compact: true);
        tokens.IsEnabled = false;
        tokens.Opacity = 0.42;
        tokens.ToolTip = "Token history is not available yet. This view never estimates tokens from quota percentages.";
        ToolTipService.SetShowOnDisabled(tokens, true);
        metrics.Children.Add(tokens);
        Grid.SetColumn(metrics, 1);
        row.Children.Add(metrics);
        return row;
    }

    private StackPanel BuildSeriesTabs(IReadOnlyList<PlanUsageSeries> series)
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in series)
        {
            var selected = item.Id == this.selectedSeriesId;
            var button = this.CreateTabButton(
                this.IconLabel(item.Title.Contains("Weekly", StringComparison.OrdinalIgnoreCase) ? CalendarGlyph : ClockGlyph, item.Title, selected),
                selected,
                compact: true);
            button.Click += (_, _) =>
            {
                this.selectedSeriesId = item.Id;
                this.selectedDate = null;
                this.selectedMonth = null;
                this.viewMode = ActivityViewMode.Overview;
                this.Refresh();
            };
            AutomationProperties.SetName(button, $"Show {item.Title} activity");
            tabs.Children.Add(button);
        }

        return tabs;
    }

    private UIElement BuildTimeframeHeader(
        ActivityOverview activity,
        ActivityDay selected,
        ActivityWeek week,
        ActivityMonth month)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (this.viewMode != ActivityViewMode.Overview)
        {
            var back = this.CreateTabButton(
                this.IconLabel(BackGlyph, "Back", true),
                selected: false,
                compact: true);
            back.Margin = new Thickness(0, 0, 14, 0);
            back.VerticalAlignment = VerticalAlignment.Center;
            back.Click += (_, _) => this.GoBack();
            back.ToolTip = $"Back to {this.ParentViewName()}";
            AutomationProperties.SetName(back, $"Back to {this.ParentViewName()}");
            header.Children.Add(back);
        }

        var copy = new StackPanel();
        copy.Children.Add(this.TimeframeLabel(
            this.viewMode.ToString().ToUpperInvariant(),
            this.viewMode is ActivityViewMode.Overview or ActivityViewMode.Month ? CalendarGlyph : ClockGlyph));
        var title = this.viewMode switch
        {
            ActivityViewMode.Overview => null,
            ActivityViewMode.Month => month.StartsOn.ToString("MMMM yyyy", UiCulture),
            ActivityViewMode.Week => $"{week.StartsOn.ToString("MMMM d", UiCulture)}–{week.StartsOn.AddDays(6).ToString("MMMM d", UiCulture)}",
            _ => selected.Date.ToString("dddd, MMMM d", UiCulture),
        };
        var guidance = this.viewMode switch
        {
            ActivityViewMode.Overview => "Hover over a cube for details, or select it to open that month",
            ActivityViewMode.Month => "Select a week to inspect its daily activity",
            ActivityViewMode.Week => "Select a day to inspect its hourly activity",
            _ => "Hourly activity observed during this day",
        };
        if (title is not null)
        {
            copy.Children.Add(new TextBlock
            {
                Text = title,
                Margin = new Thickness(0, 5, 0, 2),
                FontFamily = DisplayFont,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = this.Brush("StatisticsForeground"),
            });
        }

        copy.Children.Add(new TextBlock
        {
            Text = guidance,
            Margin = this.viewMode == ActivityViewMode.Overview ? new Thickness(0, 4, 0, 0) : new Thickness(0),
            FontSize = 12,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        if (this.viewMode == ActivityViewMode.Month)
        {
            var navigation = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var minimumMonth = new DateOnly(activity.StartsOn.Year, activity.StartsOn.Month, 1);
            var maximumMonth = new DateOnly(activity.EndsOn.Year, activity.EndsOn.Month, 1);
            var previous = this.CreateTabButton(this.IconLabel(BackGlyph, "Previous"), selected: false, compact: true);
            previous.IsEnabled = month.StartsOn > minimumMonth;
            previous.Click += (_, _) => this.NavigateMonth(month.StartsOn.AddMonths(-1));
            navigation.Children.Add(previous);
            var next = this.CreateTabButton(this.IconLabel("\uE72A", "Next"), selected: false, compact: true);
            next.IsEnabled = month.StartsOn < maximumMonth;
            next.Click += (_, _) => this.NavigateMonth(month.StartsOn.AddMonths(1));
            navigation.Children.Add(next);
            Grid.SetColumn(navigation, 2);
            header.Children.Add(navigation);
        }

        return header;
    }

    private UIElement BuildGeneralSummary(ProviderDescriptor descriptor, ActivityOverview activity, Color accent) => this.BuildSummary(
        descriptor,
        accent,
        "OBSERVED",
        Quota(activity.Total),
        $"{activity.CoveredDays} days recorded locally",
        (CalendarGlyph, "Active days", activity.ActiveDays.ToString(UiCulture), "in the last 52 weeks"),
        (LogoImages.StatisticsGlyph, "Daily average", Quota(activity.DailyAverage), "per observed day"),
        ("\uE9D9", "Busiest day", activity.BusiestDay is { } day ? day.Date.ToString("MMM d", UiCulture) : "—", activity.BusiestDay is { } busiest ? Quota(busiest.Value) : "no activity yet"));

    private UIElement BuildMonthSummary(ProviderDescriptor descriptor, ActivityMonth month, Color accent) => this.BuildSummary(
        descriptor,
        accent,
        "MONTH TOTAL",
        Quota(month.Total),
        $"{month.CoveredDays} days recorded locally",
        (CalendarGlyph, "Active days", month.ActiveDays.ToString(UiCulture), $"of {month.Days.Count} elapsed days"),
        (LogoImages.StatisticsGlyph, "Daily average", Quota(month.DailyAverage), "per observed day"),
        ("\uE9D9", "Busiest week", month.BusiestWeek is { } week ? week.StartsOn.ToString("MMM d", UiCulture) : "—", month.BusiestWeek is { } busiest ? Quota(busiest.Total) : "no activity yet"));

    private UIElement BuildWeekSummary(ProviderDescriptor descriptor, ActivityOverview activity, ActivityWeek week, Color accent)
    {
        var previous = activity.WeekContaining(week.StartsOn.AddDays(-7));
        return this.BuildSummary(
            descriptor,
            accent,
            "WEEK TOTAL",
            Quota(week.Total),
            $"{week.CoveredDays} days recorded locally",
            (CalendarGlyph, "Active days", week.ActiveDays.ToString(UiCulture), "of 7 days"),
            ("\uE7BA", "Previous week", previous.CoveredDays > 0 ? Difference(week.Total, previous.Total) : "No comparison", "observed quota points"),
            ("\uE9D9", "Busiest day", week.BusiestDay is { } day ? day.Date.ToString("ddd", UiCulture) : "—", week.BusiestDay is { } busiest ? Quota(busiest.Value) : "no activity yet"));
    }

    private UIElement BuildDaySummary(ProviderDescriptor descriptor, ActivityOverview activity, ActivityDay selected, Color accent)
    {
        var peak = selected.Hours.OrderByDescending(hour => hour.Value).FirstOrDefault();
        return this.BuildSummary(
            descriptor,
            accent,
            "DAY TOTAL",
            selected.HasCoverage ? Quota(selected.Value) : "—",
            selected.HasCoverage ? $"{selected.ObservationCount} local observations" : "no observations",
            (ClockGlyph, "Active hours", selected.ActiveHours.ToString(UiCulture), "of 24 hours"),
            ("\uE7BA", "Previous day", PreviousComparison(selected, activity.Day(selected.Date.AddDays(-1))), "observed quota points"),
            ("\uE9D9", "Busiest hour", peak is { Value: > 0.001 } ? $"{peak.Hour:00}:00" : "—", peak is { Value: > 0.001 } ? Quota(peak.Value) : "no activity yet"));
    }

    private UIElement BuildSummary(
        ProviderDescriptor descriptor,
        Color accent,
        string primaryLabel,
        string primaryValue,
        string primaryDetail,
        params (string Icon, string Label, string Value, string Detail)[] facts)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 28), MinHeight = 96 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(255) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(this.BuildProviderArtwork(descriptor, accent));

        var primary = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        primary.Children.Add(new TextBlock
        {
            Text = primaryLabel,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        var value = new TextBlock
        {
            Text = primaryValue,
            Margin = new Thickness(0, 1, 0, 0),
            FontFamily = DisplayFont,
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(this.isDark ? Blend(accent, Colors.White, 0.18) : accent),
        };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        primary.Children.Add(value);
        primary.Children.Add(new TextBlock
        {
            Text = primaryDetail,
            FontSize = 11.5,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        Grid.SetColumn(primary, 1);
        grid.Children.Add(primary);

        var factList = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var fact in facts)
        {
            factList.Children.Add(this.BuildSummaryFact(fact.Icon, fact.Label, fact.Value, fact.Detail, accent));
        }

        Grid.SetColumn(factList, 2);
        grid.Children.Add(factList);

        return grid;
    }

    private UIElement BuildOverviewSection(ActivityOverview activity, ActivityDay selected, Color accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(this.SectionHeader(CalendarGlyph, "Activity calendar", null, accent, this.BuildScaleButtons()));
        var calendar = new ActivityCalendarControl(
            activity.Days,
            selected.Date,
            activity.EndsOn,
            accent,
            this.isDark,
            this.scaleMode)
        {
            Margin = new Thickness(0, 5, 0, 5),
        };
        calendar.DateSelected += this.SelectMonth;
        stack.Children.Add(calendar);

        var legend = new Grid { Margin = new Thickness(30, 2, 0, 4) };
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        legend.Children.Add(new TextBlock
        {
            Text = this.scaleMode == ActivityScaleMode.Personal
                ? "Personal scale adapts to your active days"
                : "Fixed scale: 5 · 15 · 30 quota points",
            FontSize = 11.5,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        var scale = new StackPanel { Orientation = Orientation.Horizontal };
        scale.Children.Add(this.LegendText("Less"));
        for (var level = 1; level <= 4; level++)
        {
            scale.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(4, 1, 0, 0),
                Background = new SolidColorBrush(IntensityColor(accent, level)),
            });
        }

        scale.Children.Add(this.LegendText("More", new Thickness(6, 0, 0, 0)));
        Grid.SetColumn(scale, 1);
        legend.Children.Add(scale);
        stack.Children.Add(legend);
        return stack;
    }

    private UIElement BuildMonthSection(ActivityMonth month, Color accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(this.SectionHeader(LogoImages.StatisticsGlyph, "Weekly activity", "Select a week to see its days", accent));
        var bars = month.Weeks.Select(week => new ActivityBar(
            week.StartsOn.ToString("MMM d", UiCulture),
            week.Total,
            $"Week of {week.StartsOn.ToString("MMMM d", UiCulture)}: {Quota(week.Total)} · {week.ActiveDays} active days")).ToArray();
        var chart = new ActivityBarChart(bars, accent, this.isDark, interactive: true, actionLabel: "Open week")
        {
            Margin = new Thickness(0, 8, 0, 12),
        };
        chart.BarSelected += index => this.SelectWeek(month.Weeks[index].StartsOn);
        stack.Children.Add(chart);
        return stack;
    }

    private UIElement BuildWeekSection(ActivityOverview activity, ActivityWeek week, Color accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(this.SectionHeader(LogoImages.StatisticsGlyph, "Daily activity", "Select a day to see its hours", accent));
        var bars = week.Days.Select(day => new ActivityBar(
            day.Date.ToString("ddd", UiCulture),
            day.Value,
            $"{day.Date.ToString("dddd, MMM d", UiCulture)}: {Quota(day.Value)} · {day.ActiveHours} active hours",
            day.Date <= activity.EndsOn)).ToArray();
        var chart = new ActivityBarChart(bars, accent, this.isDark, interactive: true, actionLabel: "Open day")
        {
            Margin = new Thickness(0, 8, 0, 12),
        };
        chart.BarSelected += index => this.SelectDate(week.StartsOn.AddDays(index));
        stack.Children.Add(chart);
        return stack;
    }

    private UIElement BuildDaySection(ActivityDay selected, Color accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(this.SectionHeader(ClockGlyph, "Hourly activity", "Local time", accent));
        var bars = selected.Hours.Select(hour => new ActivityBar(
            hour.Hour.ToString("00", UiCulture),
            hour.Value,
            $"{hour.Hour:00}:00–{hour.Hour:00}:59: {Quota(hour.Value)} · {hour.ObservationCount} observations")).ToArray();
        stack.Children.Add(new ActivityBarChart(bars, accent, this.isDark, interactive: false)
        {
            Margin = new Thickness(0, 8, 0, 12),
        });
        return stack;
    }

    private UIElement BuildProviderArtwork(ProviderDescriptor descriptor, Color accent)
    {
        var artwork = new Grid { Width = 78, Height = 78, HorizontalAlignment = HorizontalAlignment.Left };
        FrameworkElement logo = LogoImages.Get(descriptor.Branding.GlyphKey, this.isDark) is { } source
            ? new Image { Source = source, Width = 46, Height = 46 }
            : LogoImages.IconGlyph(LogoImages.StatisticsGlyph, 38);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        logo.VerticalAlignment = VerticalAlignment.Center;
        logo.Effect = new DropShadowEffect
        {
            BlurRadius = 13,
            Direction = 270,
            ShadowDepth = 3,
            Opacity = 0.34,
            Color = this.isDark ? Colors.Black : Blend(accent, Colors.Black, 0.58),
        };
        artwork.Children.Add(logo);
        return artwork;
    }

    private UIElement BuildSummaryFact(
        string icon,
        string label,
        string value,
        string detail,
        Color accent)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = LogoImages.IconGlyph(icon, 13);
        glyph.HorizontalAlignment = HorizontalAlignment.Left;
        glyph.Foreground = new SolidColorBrush(accent);
        row.Children.Add(glyph);
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsMutedForeground"),
        };
        Grid.SetColumn(labelText, 1);
        row.Children.Add(labelText);
        var reading = new TextBlock { FontSize = 11.5, Foreground = this.Brush("StatisticsMutedForeground") };
        reading.Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold, Foreground = this.Brush("StatisticsForeground") });
        reading.Inlines.Add(new Run($"  {detail}"));
        Grid.SetColumn(reading, 2);
        row.Children.Add(reading);
        return row;
    }

    private StackPanel BuildScaleButtons()
    {
        var scales = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var mode in new[] { ActivityScaleMode.Personal, ActivityScaleMode.Fixed })
        {
            var button = this.CreateTabButton(
                this.IconLabel(
                    mode == ActivityScaleMode.Personal ? ScaleGlyph : "\uE72E",
                    mode == ActivityScaleMode.Personal ? "Personal" : "Fixed",
                    mode == this.scaleMode),
                mode == this.scaleMode,
                compact: true);
            button.Click += (_, _) =>
            {
                this.scaleMode = mode;
                this.Refresh();
            };
            button.ToolTip = mode == ActivityScaleMode.Personal
                ? "Levels follow your own active-day distribution. Best for seeing patterns."
                : "Levels use 5, 15, and 30 observed quota-point thresholds. Best for comparison.";
            scales.Children.Add(button);
        }

        return scales;
    }

    private Grid SectionHeader(string icon, string title, string? detail, Color accent, UIElement? trailing = null)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = LogoImages.IconGlyph(icon, 15);
        glyph.HorizontalAlignment = HorizontalAlignment.Left;
        glyph.Foreground = new SolidColorBrush(accent);
        header.Children.Add(glyph);
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
        };
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);
        UIElement right = trailing ?? new TextBlock
        {
            Text = detail ?? string.Empty,
            FontSize = 11.5,
            Foreground = this.Brush("StatisticsMutedForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(right, 2);
        header.Children.Add(right);
        return header;
    }

    private UIElement BuildEmptyState()
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
        };
        stack.Children.Add(LogoImages.IconGlyph(LogoImages.StatisticsGlyph, 34));
        stack.Children.Add(new TextBlock
        {
            Text = "Activity starts here",
            Margin = new Thickness(0, 16, 0, 7),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Foreground = this.Brush("StatisticsForeground"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "CodexWinBar builds the calendar from successful provider refreshes. Keep it running while you work to reveal daily, weekly, and hourly patterns.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            LineHeight = 20,
            FontSize = 13,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        return stack;
    }

    private Button CreateTabButton(object content, bool selected, bool compact)
    {
        var button = new Button
        {
            Content = content,
            Padding = compact ? new Thickness(11, 6, 11, 6) : new Thickness(14, 8, 14, 8),
            MinHeight = compact ? 32 : 38,
            Margin = new Thickness(0, 0, 6, 0),
            Background = selected ? this.Brush("StatisticsSelectedBackground") : this.Brush("StatisticsTabBackground"),
            BorderBrush = selected ? this.Brush("StatisticsSelectedBorder") : this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            Foreground = selected ? this.Brush("StatisticsForeground") : this.Brush("StatisticsMutedForeground"),
            Cursor = Cursors.Hand,
            Template = CreateTabTemplate(),
        };
        button.MouseEnter += (_, _) =>
        {
            if (button.IsEnabled)
            {
                button.Background = this.Brush("StatisticsHoverBackground");
            }
        };
        button.MouseLeave += (_, _) => button.Background = selected
            ? this.Brush("StatisticsSelectedBackground")
            : this.Brush("StatisticsTabBackground");
        return button;
    }

    private static ControlTemplate CreateTabTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
        border.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
        border.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.MarginProperty, TemplateBinding("Padding"));
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "border"));
        template.Triggers.Add(focus);
        return template;
    }

    private static ControlTemplate CreateProviderButtonTemplate()
    {
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.MarginProperty, TemplateBinding("Padding"));
        surface.AppendChild(presenter);
        return new ControlTemplate(typeof(ButtonBase)) { VisualTree = surface };
    }

    private TextBlock LegendText(string text, Thickness? margin = null) => new()
    {
        Text = text,
        Margin = margin ?? new Thickness(0),
        FontSize = 11,
        Foreground = this.Brush("StatisticsMutedForeground"),
    };

    private StackPanel TimeframeLabel(string text, string icon)
    {
        var label = this.IconLabel(icon, text, true, iconSize: 11);
        foreach (var child in label.Children.OfType<TextBlock>())
        {
            child.Foreground = this.Brush("StatisticsMutedForeground");
        }

        return label;
    }

    private StackPanel IconLabel(string glyph, string text, bool strong = false, double iconSize = 12)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = LogoImages.IconGlyph(glyph, iconSize);
        icon.Margin = new Thickness(0, 0, 7, 0);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    private static System.Windows.Data.Binding TemplateBinding(string path) => new(path)
    {
        RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
    };

    private void SelectDate(DateOnly date)
    {
        this.selectedDate = date;
        this.viewMode = ActivityViewMode.Day;
        this.Refresh();
    }

    private void SelectMonth(DateOnly date)
    {
        this.selectedMonth = new DateOnly(date.Year, date.Month, 1);
        this.selectedDate = date;
        this.viewMode = ActivityViewMode.Month;
        this.Refresh();
    }

    private void SelectWeek(DateOnly weekStart)
    {
        var monthStart = this.selectedMonth!.Value;
        this.selectedDate = weekStart < monthStart ? monthStart : weekStart;
        this.viewMode = ActivityViewMode.Week;
        this.Refresh();
    }

    private void NavigateMonth(DateOnly month)
    {
        this.selectedMonth = new DateOnly(month.Year, month.Month, 1);
        this.selectedDate = this.selectedMonth;
        this.viewMode = ActivityViewMode.Month;
        this.Refresh();
    }

    private void GoBack()
    {
        this.viewMode = this.viewMode switch
        {
            ActivityViewMode.Day => ActivityViewMode.Week,
            ActivityViewMode.Week => ActivityViewMode.Month,
            ActivityViewMode.Month => ActivityViewMode.Overview,
            _ => ActivityViewMode.Overview,
        };
        this.Refresh();
    }

    private string ParentViewName() => this.viewMode switch
    {
        ActivityViewMode.Day => "week",
        ActivityViewMode.Week => "month",
        _ => "overview",
    };

    private void OnStatisticsChanged()
    {
        _ = this.Dispatcher.BeginInvoke(this.Refresh);
    }

    private SolidColorBrush Brush(string key) => (SolidColorBrush)this.Resources[key];

    private static string Quota(double value) => $"{value:0.#} pts";

    private static string Difference(double current, double previous)
    {
        var difference = current - previous;
        if (Math.Abs(difference) < 0.05)
        {
            return "About the same";
        }

        return difference > 0 ? $"+{difference:0.#} pts" : $"{difference:0.#} pts";
    }

    private static string PreviousComparison(ActivityDay selected, ActivityDay? previous)
    {
        if (previous is not { HasCoverage: true })
        {
            return "No comparison";
        }

        var difference = selected.Value - previous.Value;
        if (Math.Abs(difference) < 0.05)
        {
            return "About the same";
        }

        return difference > 0 ? $"+{difference:0.#} pts" : $"{difference:0.#} pts";
    }

    private Color IntensityColor(Color accent, int level)
    {
        var alpha = level switch
        {
            1 => 74,
            2 => 124,
            3 => 184,
            _ => 242,
        };
        return Color.FromArgb((byte)alpha, accent.R, accent.G, accent.B);
    }

    private static Color Blend(Color left, Color right, double amount) => Color.FromRgb(
        (byte)Math.Round(left.R + ((right.R - left.R) * amount)),
        (byte)Math.Round(left.G + ((right.G - left.G) * amount)),
        (byte)Math.Round(left.B + ((right.B - left.B) * amount)));

    private sealed record ProviderButtonVisual(
        ProviderId Provider,
        FrameworkElement Logo,
        Border NameHost,
        double NameWidth,
        DropShadowEffect Shadow);

    private static int ProviderOrder(ProviderId provider) => provider switch
    {
        ProviderId.Codex => 0,
        ProviderId.Claude => 1,
        _ => 2,
    };

    private static bool SystemAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

internal static class StatisticsTheme
{
    internal static ResourceDictionary Create(bool dark)
    {
        var resources = new ResourceDictionary();
        Add(resources, "StatisticsForeground", dark ? "#F3F4F6" : "#17191D");
        Add(resources, "StatisticsWindowBackground", dark ? "#111419" : "#F4F5F7");
        Add(resources, "StatisticsMutedForeground", dark ? "#A7ADB8" : "#626874");
        Add(resources, "StatisticsCardBackground", dark ? "#991B1E24" : "#C8FFFFFF");
        Add(resources, "StatisticsCardBorder", dark ? "#33FFFFFF" : "#1F121722");
        Add(resources, "StatisticsBadgeBackground", dark ? "#331D8FFF" : "#14126FE8");
        Add(resources, "StatisticsTabBackground", dark ? "#551B1E24" : "#80FFFFFF");
        Add(resources, "StatisticsSelectedBackground", dark ? "#2BFFFFFF" : "#E8FFFFFF");
        Add(resources, "StatisticsSelectedBorder", dark ? "#66FFFFFF" : "#42121722");
        Add(resources, "StatisticsHoverBackground", dark ? "#3DFFFFFF" : "#F4FFFFFF");
        return resources;
    }

    private static void Add(ResourceDictionary resources, string key, string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        resources[key] = brush;
    }
}
