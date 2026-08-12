using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

/// <summary>Plan-utilization history window backed by locally observed provider snapshots.</summary>
public sealed class StatisticsWindow : Window
{
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static StatisticsWindow? current;
    private readonly IPlanStatisticsStore store;
    private readonly IReadOnlyDictionary<ProviderId, ProviderDescriptor> descriptors;
    private readonly StackPanel providerTabs = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl dashboardHost = new();
    private readonly bool isDark;
    private ProviderId selectedProvider = ProviderId.Codex;
    private string? selectedSeriesId;

    private StatisticsWindow(
        IPlanStatisticsStore store,
        IReadOnlyList<ProviderDescriptor> descriptors)
    {
        this.store = store;
        this.descriptors = descriptors.ToDictionary(item => item.Id);
        this.isDark = !SystemAppsUseLightTheme();

        this.Title = "CodexWinBar Statistics";
        this.Width = 860;
        this.Height = 650;
        this.MinWidth = 720;
        this.MinHeight = 540;
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

        var header = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Statistics",
            FontFamily = DisplayFont,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
        });
        heading.Children.Add(new TextBlock
        {
            Text = "How much of each plan window you actually use",
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 13,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        header.Children.Add(heading);

        var localBadge = new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(12),
            Background = this.Brush("StatisticsBadgeBackground"),
            BorderBrush = this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "LOCAL HISTORY",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = this.Brush("StatisticsMutedForeground"),
            },
        };
        Grid.SetColumn(localBadge, 1);
        header.Children.Add(localBadge);
        root.Children.Add(header);

        var providerScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = this.providerTabs,
            Margin = new Thickness(0, 0, 0, 18),
        };
        Grid.SetRow(providerScroller, 1);
        root.Children.Add(providerScroller);

        this.dashboardHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        this.dashboardHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(this.dashboardHost, 2);
        root.Children.Add(this.dashboardHost);
        return root;
    }

    private void Refresh()
    {
        this.RefreshProviderTabs();
        var statistics = this.store.Get(this.selectedProvider);
        var series = statistics.Series.FirstOrDefault(item => item.Id == this.selectedSeriesId)
            ?? statistics.Series.FirstOrDefault();
        this.selectedSeriesId = series?.Id;
        this.dashboardHost.Content = series is null ? this.BuildEmptyState() : this.BuildDashboard(series, statistics.Series);
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
                VerticalAlignment = VerticalAlignment.Center,
            });
            var button = this.CreateTabButton(content, selected, compact: false);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Click += (_, _) =>
            {
                this.selectedProvider = provider;
                this.selectedSeriesId = null;
                this.Refresh();
            };
            AutomationProperties.SetName(button, $"{descriptor.Metadata.DisplayName} statistics");
            this.providerTabs.Children.Add(button);
        }
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
            button.Margin = new Thickness(0, 0, 6, 0);
            button.Click += (_, _) =>
            {
                this.selectedSeriesId = item.Id;
                this.Refresh();
            };
            AutomationProperties.SetName(button, $"Show {item.Title} history");
            tabs.Children.Add(button);
        }

        return tabs;
    }

    private UIElement BuildDashboard(PlanUsageSeries series, IReadOnlyList<PlanUsageSeries> allSeries)
    {
        var summary = PlanStatisticsProjection.Build(series, DateTimeOffset.UtcNow);
        var descriptor = this.descriptors[this.selectedProvider];
        var accent = Color.FromRgb(descriptor.Branding.R, descriptor.Branding.G, descriptor.Branding.B);
        var numeralAccent = this.isDark ? Blend(accent, Colors.White, 0.42) : DarkenForText(accent);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(this.BuildSeriesTabs(allSeries));

        var metrics = new Grid { Margin = new Thickness(0, 16, 0, 14) };
        for (var index = 0; index < 4; index++)
        {
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var completedCount = summary.Cycles.Count(item => !item.IsCurrent);
        var metricValues = new[]
        {
            ("CURRENT", Percent(summary.CurrentUsedPercent), summary.CurrentUsedPercent is null ? "no active cycle" : "latest observation"),
            ("AVG PEAK", Percent(summary.AveragePeakPercent), "completed cycles"),
            ("HIGHEST", Percent(summary.HighestPeakPercent), "completed cycle"),
            ("UNUSED", Percent(summary.AverageUnusedPercent), "average left"),
        };
        for (var index = 0; index < metricValues.Length; index++)
        {
            var card = this.BuildMetricCard(metricValues[index].Item1, metricValues[index].Item2, metricValues[index].Item3, numeralAccent);
            card.Margin = new Thickness(index == 0 ? 0 : 5, 0, index == metricValues.Length - 1 ? 0 : 5, 0);
            Grid.SetColumn(card, index);
            metrics.Children.Add(card);
        }

        Grid.SetRow(metrics, 1);
        root.Children.Add(metrics);

        var chartHeader = new Grid { Margin = new Thickness(18, 15, 18, 4) };
        chartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chartHeader.Children.Add(new TextBlock
        {
            Text = "Usage by quota cycle",
            FontFamily = TextFont,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Brush("StatisticsForeground"),
        });
        var cycleLabel = new TextBlock
        {
            Text = completedCount == 1 ? "1 completed cycle" : $"{completedCount} completed cycles",
            FontSize = 12,
            Foreground = this.Brush("StatisticsMutedForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cycleLabel, 1);
        chartHeader.Children.Add(cycleLabel);

        var chartLayout = new Grid();
        chartLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chartLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chartLayout.Children.Add(chartHeader);
        var chart = new PlanUsageChart(summary.Cycles, accent, this.isDark, series.WindowMinutes)
        {
            Margin = new Thickness(12, 2, 10, 8),
            MinHeight = 220,
        };
        Grid.SetRow(chart, 1);
        chartLayout.Children.Add(chart);
        var chartCard = new Border
        {
            Background = this.Brush("StatisticsCardBackground"),
            BorderBrush = this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = chartLayout,
        };
        Grid.SetRow(chartCard, 2);
        root.Children.Add(chartCard);

        var note = new TextBlock
        {
            Text = "Built from local provider refreshes · no prompts or message content stored",
            Margin = new Thickness(2, 12, 0, 0),
            FontSize = 11,
            Foreground = this.Brush("StatisticsMutedForeground"),
        };
        Grid.SetRow(note, 3);
        root.Children.Add(note);
        return root;
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
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
        };
        Typography.SetNumeralAlignment(valueText, FontNumeralAlignment.Tabular);
        stack.Children.Add(valueText);
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        return new Border
        {
            Background = this.Brush("StatisticsCardBackground"),
            BorderBrush = this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = stack,
        };
    }

    private UIElement BuildEmptyState()
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 440,
        };
        stack.Children.Add(LogoImages.IconGlyph(LogoImages.StatisticsGlyph, 34));
        stack.Children.Add(new TextBlock
        {
            Text = "History starts here",
            Margin = new Thickness(0, 16, 0, 7),
            FontFamily = TextFont,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Foreground = this.Brush("StatisticsForeground"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "CodexWinBar will build a cycle-by-cycle view as it observes successful provider refreshes. Keep it running through a few quota resets to reveal your real usage pattern.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            LineHeight = 20,
            FontSize = 13,
            Foreground = this.Brush("StatisticsMutedForeground"),
        });
        return new Border
        {
            Background = this.Brush("StatisticsCardBackground"),
            BorderBrush = this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
    }

    private Button CreateTabButton(object content, bool selected, bool compact)
    {
        var button = new Button
        {
            Content = content,
            Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(14, 8, 14, 8),
            MinHeight = compact ? 32 : 38,
            Background = selected ? this.Brush("StatisticsSelectedBackground") : this.Brush("StatisticsTabBackground"),
            BorderBrush = selected ? this.Brush("StatisticsSelectedBorder") : this.Brush("StatisticsCardBorder"),
            BorderThickness = new Thickness(1),
            Foreground = selected ? this.Brush("StatisticsForeground") : this.Brush("StatisticsMutedForeground"),
            Cursor = Cursors.Hand,
            Template = CreateTabTemplate(),
        };
        button.MouseEnter += (_, _) => button.Background = this.Brush("StatisticsHoverBackground");
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

    private static System.Windows.Data.Binding TemplateBinding(string path) => new(path)
    {
        RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
    };

    private void OnStatisticsChanged()
    {
        _ = this.Dispatcher.BeginInvoke(this.Refresh);
    }

    private SolidColorBrush Brush(string key) => (SolidColorBrush)this.Resources[key];

    private static string Percent(double? value) => value is { } percent ? $"{percent:0}%" : "—";

    private static Color Blend(Color left, Color right, double amount) => Color.FromRgb(
        (byte)Math.Round(left.R + ((right.R - left.R) * amount)),
        (byte)Math.Round(left.G + ((right.G - left.G) * amount)),
        (byte)Math.Round(left.B + ((right.B - left.B) * amount)));

    private static Color DarkenForText(Color color)
    {
        var current = color;
        while (RelativeLuminance(current) > 0.20)
        {
            current = Blend(current, Colors.Black, 0.12);
        }

        return current;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

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

internal sealed class PlanUsageChart : FrameworkElement
{
    private static readonly FontFamily ChartFont = new("Segoe UI Variable Text, Segoe UI");
    private readonly IReadOnlyList<PlanCycle> cycles;
    private readonly Color accent;
    private readonly bool isDark;
    private readonly int windowMinutes;
    private int tooltipIndex = -1;

    internal PlanUsageChart(IReadOnlyList<PlanCycle> cycles, Color accent, bool isDark, int windowMinutes)
    {
        this.cycles = cycles;
        this.accent = accent;
        this.isDark = isDark;
        this.windowMinutes = windowMinutes;
        this.Focusable = false;
        this.Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "Quota cycle usage chart");
        AutomationProperties.SetHelpText(
            this,
            string.Join(
                "; ",
                cycles.Select(cycle => $"{cycle.CycleEndsAt.LocalDateTime:d}: {cycle.PeakUsedPercent:0}% peak")));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var plot = this.PlotRect();
        if (plot.Width <= 0 || plot.Height <= 0 || this.cycles.Count == 0)
        {
            return;
        }

        var muted = this.isDark ? Color.FromRgb(139, 145, 157) : Color.FromRgb(96, 101, 112);
        var grid = this.isDark ? Color.FromArgb(75, 255, 255, 255) : Color.FromArgb(42, 30, 35, 45);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, this.ActualWidth, this.ActualHeight)));
        foreach (var level in new[] { 100, 50, 0 })
        {
            var y = plot.Bottom - (plot.Height * level / 100.0);
            drawingContext.DrawLine(new Pen(new SolidColorBrush(grid), 1), new Point(plot.Left, y), new Point(plot.Right, y));
            this.DrawText(drawingContext, $"{level}%", muted, 11.5, new Point(0, y - 8), dpi);
        }

        var guideY = plot.Bottom - (plot.Height * 0.8);
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(120, this.accent.R, this.accent.G, this.accent.B)), 1)
        {
            DashStyle = DashStyles.Dash,
        };
        drawingContext.DrawLine(guidePen, new Point(plot.Left, guideY), new Point(plot.Right, guideY));

        var slot = plot.Width / this.cycles.Count;
        var barWidth = Math.Clamp(slot * 0.58, 5, 22);
        for (var index = 0; index < this.cycles.Count; index++)
        {
            var cycle = this.cycles[index];
            var displayPercent = cycle.IsCurrent ? cycle.LatestUsedPercent : cycle.PeakUsedPercent;
            var height = Math.Max(2, plot.Height * Math.Clamp(displayPercent, 0, 100) / 100.0);
            var x = plot.Left + (slot * index) + ((slot - barWidth) / 2);
            var rect = new Rect(x, plot.Bottom - height, barWidth, height);
            var alpha = cycle.IsCurrent ? (byte)255 : (byte)178;
            var fill = new SolidColorBrush(Color.FromArgb(alpha, this.accent.R, this.accent.G, this.accent.B));
            drawingContext.DrawRoundedRectangle(fill, null, rect, 3, 3);
            if (cycle.IsCurrent)
            {
                drawingContext.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(this.accent), 1.5), rect, 3, 3);
            }
        }

        var labelStep = Math.Max(1, (int)Math.Ceiling(this.cycles.Count / 6.0));
        for (var index = 0; index < this.cycles.Count; index += labelStep)
        {
            var cycle = this.cycles[index];
            var label = this.windowMinutes <= 1440
                ? cycle.CycleEndsAt.LocalDateTime.ToString("ddd HH:mm", CultureInfo.CurrentCulture)
                : cycle.CycleEndsAt.LocalDateTime.ToString("MMM d", CultureInfo.CurrentCulture);
            var formatted = this.FormatText(label, muted, 11.5, dpi);
            var center = plot.Left + (slot * index) + (slot / 2);
            var maxX = Math.Max(plot.Left, plot.Right - formatted.Width);
            var x = Math.Clamp(center - (formatted.Width / 2), plot.Left, maxX);
            drawingContext.DrawText(formatted, new Point(x, plot.Bottom + 8));
        }


        drawingContext.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = this.PlotRect();
        var point = e.GetPosition(this);
        if (!plot.Contains(point) || this.cycles.Count == 0)
        {
            this.tooltipIndex = -1;
            this.ToolTip = null;
            return;
        }

        var index = Math.Clamp((int)((point.X - plot.Left) / (plot.Width / this.cycles.Count)), 0, this.cycles.Count - 1);
        if (index == this.tooltipIndex)
        {
            return;
        }

        this.tooltipIndex = index;
        var cycle = this.cycles[index];
        this.ToolTip = cycle.IsCurrent
            ? $"{cycle.CycleEndsAt.LocalDateTime:g}\nLatest used: {cycle.LatestUsedPercent:0}%\nCurrent cycle"
            : $"{cycle.CycleEndsAt.LocalDateTime:g}\nPeak used: {cycle.PeakUsedPercent:0}%";
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private Rect PlotRect() => new(42, 12, Math.Max(0, this.ActualWidth - 58), Math.Max(0, this.ActualHeight - 48));

    private void DrawText(
        DrawingContext drawingContext,
        string text,
        Color color,
        double size,
        Point origin,
        double pixelsPerDip)
    {
        drawingContext.DrawText(this.FormatText(text, color, size, pixelsPerDip), origin);
    }

    private FormattedText FormatText(string text, Color color, double size, double pixelsPerDip) => new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(ChartFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            pixelsPerDip);
}

internal static class StatisticsTheme
{
    internal static ResourceDictionary Create(bool dark)
    {
        var resources = new ResourceDictionary();
        Add(resources, "StatisticsForeground", dark ? "#F3F4F6" : "#17191D");
        Add(resources, "StatisticsMutedForeground", dark ? "#A7ADB8" : "#626874");
        Add(resources, "StatisticsCardBackground", dark ? "#991B1E24" : "#B8FFFFFF");
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
