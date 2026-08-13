using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CodexWinBar.App.Assets;
using CodexWinBar.App.Interop;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;
using Microsoft.Win32;

namespace CodexWinBar.App.Statistics;

/// <summary>Locally observed provider activity with calendar, weekly, and hourly drill-downs.</summary>
public sealed class StatisticsWindow : Window
{
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
    private ActivityScaleMode scaleMode = ActivityScaleMode.Personal;

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
        this.Background = Brushes.Transparent;
        this.Content = this.BuildRoot();

        this.SourceInitialized += (_, _) => WpfDwm.ApplyWindowChrome(this, this.isDark);
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
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Activity",
            FontFamily = DisplayFont,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Your observed AI usage, from the year down to the hour",
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 13,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        header.Children.Add(heading);
        var localBadge = this.Badge("LOCAL HISTORY");
        Grid.SetColumn(localBadge, 1);
        header.Children.Add(localBadge);
        root.Children.Add(header);

        var providers = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = this.providerTabs,
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
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (LogoImages.Get(descriptor.Branding.GlyphKey, this.isDark) is { } source)
            {
                content.Children.Add(new Image
                {
                    Source = source,
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(0, 0, 8, 0),
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = descriptor.Metadata.DisplayName,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = selected ? this.Brush("StatisticsForeground") : this.Brush("StatisticsMutedForeground"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var button = this.CreateTabButton(content, selected, compact: false);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Click += (_, _) =>
            {
                this.selectedProvider = provider;
                this.selectedSeriesId = null;
                this.selectedDate = null;
                this.Refresh();
            };
            AutomationProperties.SetName(button, $"{descriptor.Metadata.DisplayName} activity");
            this.providerTabs.Children.Add(button);
        }
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
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(this.BuildControlRow(allSeries));
        root.Children.Add(this.BuildOverviewMetrics(activity, accent));
        root.Children.Add(this.BuildCalendarCard(activity, selected, accent));
        root.Children.Add(this.BuildWeekCard(week, selected, accent));
        root.Children.Add(this.BuildDayCard(activity, selected, accent));
        root.Children.Add(new TextBlock
        {
            Text = "Activity is derived from successful local refreshes. Quota points are new observed highs in the selected limit, not token counts. The first reading in each quota cycle is a baseline, so earlier usage is not assigned to an hour. Missing observations remain unfilled.",
            Margin = new Thickness(2, 12, 2, 4),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        return root;
    }

    private UIElement BuildControlRow(IReadOnlyList<PlanUsageSeries> series)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16), MinHeight = 34 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(this.BuildSeriesTabs(series));

        var metrics = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        metrics.Children.Add(this.CreateTabButton(new TextBlock { Text = "Quota", FontWeight = FontWeights.SemiBold }, selected: true, compact: true));
        var tokens = this.CreateTabButton(new TextBlock { Text = "Tokens" }, selected: false, compact: true);
        tokens.IsEnabled = false;
        tokens.Opacity = 0.42;
        tokens.ToolTip = "Token history is not available yet. This view never estimates tokens from quota percentages.";
        ToolTipService.SetShowOnDisabled(tokens, true);
        metrics.Children.Add(tokens);
        Grid.SetColumn(metrics, 1);
        row.Children.Add(metrics);

        var scales = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var mode in new[] { ActivityScaleMode.Personal, ActivityScaleMode.Fixed })
        {
            var button = this.CreateTabButton(
                new TextBlock
                {
                    Text = mode == ActivityScaleMode.Personal ? "Personal scale" : "Fixed scale",
                    FontWeight = mode == this.scaleMode ? FontWeights.SemiBold : FontWeights.Normal,
                },
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

        Grid.SetColumn(scales, 2);
        row.Children.Add(scales);
        return row;
    }

    private StackPanel BuildSeriesTabs(IReadOnlyList<PlanUsageSeries> series)
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in series)
        {
            var selected = item.Id == this.selectedSeriesId;
            var button = this.CreateTabButton(new TextBlock
            {
                Text = item.Title,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            }, selected, compact: true);
            button.Click += (_, _) =>
            {
                this.selectedSeriesId = item.Id;
                this.selectedDate = null;
                this.Refresh();
            };
            AutomationProperties.SetName(button, $"Show {item.Title} activity");
            tabs.Children.Add(button);
        }

        return tabs;
    }

    private UIElement BuildOverviewMetrics(ActivityOverview activity, Color accent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (var index = 0; index < 4; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var values = new[]
        {
            ("OBSERVED", Quota(activity.Total), "quota activity"),
            ("ACTIVE DAYS", activity.ActiveDays.ToString(UiCulture), $"of {activity.CoveredDays} observed"),
            ("DAILY AVG", Quota(activity.DailyAverage), "per observed day"),
            ("BUSIEST DAY", activity.BusiestDay is { } day ? day.Date.ToString("MMM d", UiCulture) : "—", activity.BusiestDay is { } busiest ? Quota(busiest.Value) : "no activity yet"),
        };
        for (var index = 0; index < values.Length; index++)
        {
            var card = this.BuildMetricCard(values[index].Item1, values[index].Item2, values[index].Item3, accent);
            card.Margin = new Thickness(index == 0 ? 0 : 5, 0, index == values.Length - 1 ? 0 : 5, 0);
            Grid.SetColumn(card, index);
            grid.Children.Add(card);
        }

        return grid;
    }

    private UIElement BuildCalendarCard(ActivityOverview activity, ActivityDay selected, Color accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(this.CardHeader("Activity calendar", "Last 52 weeks"));
        var calendar = new ActivityCalendarControl(
            activity.Days,
            selected.Date,
            activity.EndsOn,
            accent,
            this.isDark,
            this.scaleMode)
        {
            Margin = new Thickness(18, 2, 18, 2),
        };
        calendar.DateSelected += this.SelectDate;
        stack.Children.Add(calendar);

        var legend = new Grid { Margin = new Thickness(18, 2, 18, 15) };
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
        return this.Card(stack, new Thickness(0, 0, 0, 14));
    }

    private UIElement BuildWeekCard(ActivityWeek week, ActivityDay selected, Color accent)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        var chartStack = new StackPanel();
        chartStack.Children.Add(this.CardHeader(
            "Weekly view",
            $"{week.StartsOn.ToString("MMM d", UiCulture)} – {week.StartsOn.AddDays(6).ToString("MMM d", UiCulture)}"));
        var bars = week.Days.Select(day => new ActivityBar(
            day.Date.ToString("ddd", UiCulture),
            day.Value,
            $"{day.Date.ToString("dddd, MMM d", UiCulture)}: {Quota(day.Value)} · {day.ActiveHours} active hours")).ToArray();
        var chart = new ActivityBarChart(bars, (int)selected.Date.DayOfWeek, accent, this.isDark)
        {
            Margin = new Thickness(14, 0, 12, 14),
        };
        chart.BarSelected += index => this.SelectDate(week.StartsOn.AddDays(index));
        chartStack.Children.Add(chart);
        layout.Children.Add(chartStack);

        var stats = this.DetailStats(
            "WEEK SUMMARY",
            ("Total activity", Quota(week.Total)),
            ("Active days", $"{week.ActiveDays} / 7"),
            ("Observed days", $"{week.CoveredDays} / 7"),
            ("Busiest day", week.BusiestDay is { } day ? $"{day.Date.ToString("ddd", UiCulture)} · {Quota(day.Value)}" : "—"));
        stats.Margin = new Thickness(8, 48, 18, 18);
        Grid.SetColumn(stats, 1);
        layout.Children.Add(stats);
        return this.Card(layout, new Thickness(0, 0, 0, 14));
    }

    private UIElement BuildDayCard(ActivityOverview activity, ActivityDay selected, Color accent)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        var chartStack = new StackPanel();
        chartStack.Children.Add(this.CardHeader(
            "Hourly detail",
            selected.Date.ToString("dddd, MMMM d", UiCulture)));
        var bars = selected.Hours.Select(hour => new ActivityBar(
            hour.Hour.ToString("00", UiCulture),
            hour.Value,
            $"{hour.Hour:00}:00–{hour.Hour:00}:59: {Quota(hour.Value)} · {hour.ObservationCount} observations")).ToArray();
        var peak = selected.Hours.OrderByDescending(hour => hour.Value).FirstOrDefault();
        chartStack.Children.Add(new ActivityBarChart(bars, peak?.Hour ?? 0, accent, this.isDark)
        {
            Margin = new Thickness(14, 0, 12, 14),
        });
        layout.Children.Add(chartStack);

        var previous = activity.Day(selected.Date.AddDays(-1));
        var comparison = PreviousComparison(selected, previous);
        var stats = this.DetailStats(
            "DAY SUMMARY",
            ("Total activity", selected.HasCoverage ? Quota(selected.Value) : "No observations"),
            ("Active hours", selected.ActiveHours.ToString(UiCulture)),
            ("Busiest hour", peak is { Value: > 0.001 } ? $"{peak.Hour:00}:00 · {Quota(peak.Value)}" : "—"),
            ("Previous day", comparison));
        stats.Margin = new Thickness(8, 48, 18, 18);
        Grid.SetColumn(stats, 1);
        layout.Children.Add(stats);
        return this.Card(layout, new Thickness(0));
    }

    private Border BuildMetricCard(string label, string value, string detail, Color accent)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        var valueText = new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 4, 0, 1),
            FontFamily = DisplayFont,
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(this.isDark ? Blend(accent, Colors.White, 0.18) : accent),
        };
        Typography.SetNumeralAlignment(valueText, FontNumeralAlignment.Tabular);
        stack.Children.Add(valueText);
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11.5,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        return this.Card(stack, new Thickness(0));
    }

    private Border DetailStats(string title, params (string Label, string Value)[] values)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        foreach (var value in values)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = value.Label,
                FontSize = 12,
                Foreground = this.Brush("StatisticsMutedForeground"),
            });
            var text = new TextBlock
            {
                Text = value.Value,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = this.Brush("StatisticsForeground"),
            };
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            stack.Children.Add(row);
        }

        return new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = this.Brush("StatisticsInsetBackground"),
            BorderBrush = this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private Grid CardHeader(string title, string detail)
    {
        var header = new Grid { Margin = new Thickness(18, 15, 18, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
        });
        var right = new TextBlock
        {
            Text = detail,
            FontSize = 11.5,
            Foreground = this.Brush("StatisticsMutedForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(right, 1);
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
        return this.Card(stack, new Thickness(0));
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

    private Border Card(object child, Thickness margin) => new()
    {
        Margin = margin,
        Background = this.Brush("StatisticsCardBackground"),
        BorderBrush = this.Brush("StatisticsCardBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Child = (UIElement)child,
    };

    private Border Badge(string text) => new()
    {
        Padding = new Thickness(10, 5, 10, 5),
        CornerRadius = new CornerRadius(12),
        Background = this.Brush("StatisticsBadgeBackground"),
        BorderBrush = this.Brush("StatisticsCardBorder"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsMutedForeground"),
        },
    };

    private TextBlock LegendText(string text, Thickness? margin = null) => new()
    {
        Text = text,
        Margin = margin ?? new Thickness(0),
        FontSize = 11,
        Foreground = this.Brush("StatisticsMutedForeground"),
    };

    private static System.Windows.Data.Binding TemplateBinding(string path) => new(path)
    {
        RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
    };

    private void SelectDate(DateOnly date)
    {
        this.selectedDate = date;
        this.Refresh();
    }

    private void OnStatisticsChanged()
    {
        _ = this.Dispatcher.BeginInvoke(this.Refresh);
    }

    private SolidColorBrush Brush(string key) => (SolidColorBrush)this.Resources[key];

    private static string Quota(double value) => $"{value:0.#} pts";

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
        Add(resources, "StatisticsMutedForeground", dark ? "#A7ADB8" : "#626874");
        Add(resources, "StatisticsCardBackground", dark ? "#991B1E24" : "#C8FFFFFF");
        Add(resources, "StatisticsInsetBackground", dark ? "#52111419" : "#76F2F4F7");
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
