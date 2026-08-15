using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CodexWinBar.App.Statistics;

internal sealed record ActivityBar(string Label, double Value, string Description, bool IsEnabled = true);

/// <summary>A compact activity chart with one stable inspector and full-height input lanes.</summary>
internal sealed class ActivityBarChart : Grid
{
    private const double AxisWidth = 108;
    private const double PlotHeight = 176;
    // Rendered heights of the 10.5px axis label and the bordered value pill; the pill's top is
    // clamped by this height so it always stays inside the plot.
    private const double AxisLabelHeight = 15;
    private const double ValuePillHeight = 25;
    private readonly IReadOnlyList<ActivityBar> bars;
    private readonly Color accent;
    private readonly bool isDark;
    private readonly bool interactive;
    private readonly Func<double, string> valueFormatter;
    private readonly string? actionLabel;
    private readonly List<Button> lanes = [];

    internal ActivityBarChart(
        IReadOnlyList<ActivityBar> bars,
        Color accent,
        bool isDark,
        bool interactive,
        Func<double, string> valueFormatter,
        string? actionLabel = null)
    {
        this.bars = bars;
        this.accent = accent;
        this.isDark = isDark;
        this.interactive = interactive;
        this.valueFormatter = valueFormatter;
        this.actionLabel = actionLabel;
        this.MinHeight = 238;
        AutomationProperties.SetName(this, "Observed allowance activity chart");
        AutomationProperties.SetHelpText(this, interactive
            ? "Use Left and Right to inspect periods. Press Enter to open the selected period."
            : "Use Left and Right to inspect periods.");
        this.Build();
    }

    internal event Action<int>? BarSelected;

    internal event Action<int>? BarInspected;

    private void Build()
    {
        // 24 hourly lanes and 7 daily lanes plus the week inspector both have to fit the 900px
        // minimum window beside the scrollbar; the lane stays the full-height hit target.
        var laneWidth = this.bars.Count > 12 ? 28d : 56d;
        var visualBarWidth = this.bars.Count > 12 ? 12d : 32d;
        var plotWidth = Math.Max(laneWidth, laneWidth * this.bars.Count);
        var max = Math.Max(1, this.bars.Count == 0 ? 0 : this.bars.Max(bar => bar.Value));

        this.Width = AxisWidth + plotWidth;
        this.MaxWidth = AxisWidth + 768;
        this.HorizontalAlignment = HorizontalAlignment.Left;
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PlotHeight) });
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        this.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AxisWidth) });
        this.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(plotWidth) });

        var detail = new TextBlock
        {
            Text = this.bars.Count == 0
                ? "No observed activity"
                : this.interactive && this.actionLabel is not null
                    ? "Hover or focus a bar to explore"
                    : "Hover or focus a period to inspect its exact value",
            Margin = new Thickness(0, 0, 0, 7),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.MutedBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(),
        };
        Typography.SetNumeralAlignment(detail, FontNumeralAlignment.Tabular);
        AutomationProperties.SetLiveSetting(detail, AutomationLiveSetting.Polite);
        Grid.SetColumnSpan(detail, 2);
        this.Children.Add(detail);

        var axis = new Canvas { Width = AxisWidth, Height = PlotHeight };
        var axisLabels = new[]
        {
            this.AddAxisLabel(axis, this.valueFormatter(max), 0),
            this.AddAxisLabel(axis, this.valueFormatter(max / 2), (PlotHeight / 2) - 8),
            this.AddAxisLabel(axis, this.valueFormatter(0), PlotHeight - 18),
        };
        var valueText = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(this.isDark ? Colors.White : Color.FromRgb(23, 25, 29)),
        };
        Typography.SetNumeralAlignment(valueText, FontNumeralAlignment.Tabular);
        var valuePill = new Border
        {
            MinWidth = 96,
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(this.isDark ? Color.FromRgb(35, 39, 47) : Colors.White),
            BorderBrush = new SolidColorBrush(this.isDark ? Color.FromRgb(122, 130, 143) : Color.FromRgb(88, 96, 108)),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = valueText,
        };
        Canvas.SetLeft(valuePill, 0);
        Panel.SetZIndex(valuePill, 4);
        axis.Children.Add(valuePill);
        Grid.SetRow(axis, 1);
        this.Children.Add(axis);

        var plot = new Canvas
        {
            Width = plotWidth,
            Height = PlotHeight,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var y in new[] { 0d, PlotHeight / 2, PlotHeight - 1 })
        {
            plot.Children.Add(new Line
            {
                X1 = 0,
                X2 = plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(this.isDark
                    ? Color.FromArgb(46, 255, 255, 255)
                    : Color.FromArgb(32, 23, 25, 29)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }

        var guide = new Line
        {
            Stroke = new SolidColorBrush(this.isDark
                ? Color.FromArgb(145, 183, 188, 198)
                : Color.FromArgb(125, 88, 96, 108)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeDashCap = PenLineCap.Flat,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(guide, 2);
        plot.Children.Add(guide);

        void ShowDetail(string text, bool action)
        {
            var changed = detail.Text != text;
            StatisticsAccessibility.AnnounceText(detail, text);
            detail.Foreground = action ? new SolidColorBrush(this.accent) : this.MutedBrush();
            if (!changed || !SystemParameters.ClientAreaAnimation)
            {
                detail.Opacity = 1;
                return;
            }

            var translate = (TranslateTransform)detail.RenderTransform;
            translate.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(125))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
            detail.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(125))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }

        // The point marker sits where the dashed guide meets the inspected bar's top edge; the
        // window-background stroke lifts it from the bar fill in both themes.
        var marker = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(this.accent),
            Stroke = new SolidColorBrush(this.isDark ? Color.FromRgb(17, 20, 25) : Color.FromRgb(244, 245, 247)),
            StrokeThickness = 1.5,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(marker, 3);
        plot.Children.Add(marker);

        var restingBarColor = Color.FromArgb(205, this.accent.R, this.accent.G, this.accent.B);
        var inspectedBarColor = Color.FromArgb(244, this.accent.R, this.accent.G, this.accent.B);
        var inspectedLaneColor = Color.FromArgb(14, this.accent.R, this.accent.G, this.accent.B);
        var laneBrushes = new List<SolidColorBrush>();
        var barBrushes = new List<SolidColorBrush>();

        void HideInspection()
        {
            this.Fade(guide, 0);
            this.Fade(valuePill, 0);
            this.Fade(marker, 0);
            foreach (var label in axisLabels)
            {
                label.Opacity = 1;
            }

            for (var laneIndex = 0; laneIndex < laneBrushes.Count; laneIndex++)
            {
                laneBrushes[laneIndex].Color = Colors.Transparent;
                barBrushes[laneIndex].Color = restingBarColor;
            }
        }

        for (var index = 0; index < this.bars.Count; index++)
        {
            var itemIndex = index;
            var bar = this.bars[index];
            var height = bar.Value <= 0 ? 3 : Math.Max(5, (PlotHeight - 8) * bar.Value / max);
            var laneBrush = new SolidColorBrush(Colors.Transparent);
            var barBrush = new SolidColorBrush(restingBarColor);
            laneBrushes.Add(laneBrush);
            barBrushes.Add(barBrush);
            var visualBar = new Border
            {
                Width = visualBarWidth,
                Height = height,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = barBrush,
                BorderBrush = new SolidColorBrush(this.accent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(1.5, 1.5, 0, 0),
            };
            var lane = new Button
            {
                Width = laneWidth,
                Height = PlotHeight,
                Background = laneBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = visualBar,
                Template = TransparentButtonTemplate(),
                Cursor = this.interactive && bar.IsEnabled ? Cursors.Hand : Cursors.Arrow,
                IsTabStop = index == 0,
                Tag = index,
                ToolTip = this.interactive ? null : bar.Description,
            };
            if (!this.interactive)
            {
                ToolTipService.SetInitialShowDelay(lane, 400);
                ToolTipService.SetBetweenShowDelay(lane, 120);
            }
            AutomationProperties.SetName(
                lane,
                this.interactive && this.actionLabel is not null
                    ? $"{bar.Description}. {this.actionLabel}."
                    : bar.Description);

            void Inspect()
            {
                for (var laneIndex = 0; laneIndex < laneBrushes.Count; laneIndex++)
                {
                    laneBrushes[laneIndex].Color = Colors.Transparent;
                    barBrushes[laneIndex].Color = restingBarColor;
                }

                laneBrush.Color = inspectedLaneColor;
                barBrush.Color = inspectedBarColor;
                ShowDetail(
                    this.interactive && this.actionLabel is not null
                        ? $"{this.actionLabel}  →"
                        : bar.Description,
                    action: this.interactive && this.actionLabel is not null);
                valueText.Text = this.valueFormatter(bar.Value);
                var y = Math.Max(0, PlotHeight - height);
                var barNearEdge = (itemIndex * laneWidth) + ((laneWidth - visualBarWidth) / 2);
                guide.X1 = 0;
                guide.X2 = Math.Max(0, barNearEdge - 4);
                guide.Y1 = y;
                guide.Y2 = y;
                Canvas.SetLeft(marker, (itemIndex * laneWidth) + (laneWidth / 2) - (marker.Width / 2));
                Canvas.SetTop(marker, y - (marker.Height / 2));
                var pillTop = Math.Clamp(y - 11, 0, PlotHeight - ValuePillHeight);
                Canvas.SetTop(valuePill, pillTop);
                // The exact-value pill is opaque; an axis label it only partly covers would peek
                // out above or below it, so any label it touches steps aside until the pill hides.
                foreach (var label in axisLabels)
                {
                    label.Opacity = AxisLabelCollides(Canvas.GetTop(label), pillTop) ? 0 : 1;
                }

                this.Fade(valuePill, 1);
                this.Fade(guide, 1);
                this.Fade(marker, 1);
                AutomationProperties.SetItemStatus(this, bar.Description);
                this.BarInspected?.Invoke(itemIndex);
            }

            lane.MouseEnter += (_, _) => Inspect();
            lane.GotKeyboardFocus += (_, _) => Inspect();
            lane.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Escape)
                {
                    if (e.Key == Key.Escape)
                    {
                        RestoreDetail();
                    }
                    else
                    {
                        var targetIndex = e.Key switch
                        {
                            Key.Left => Math.Max(0, itemIndex - 1),
                            Key.Right => Math.Min(this.lanes.Count - 1, itemIndex + 1),
                            Key.Home => 0,
                            _ => this.lanes.Count - 1,
                        };
                        foreach (var target in this.lanes)
                        {
                            target.IsTabStop = false;
                        }

                        this.lanes[targetIndex].IsTabStop = true;
                        _ = this.lanes[targetIndex].Focus();
                    }

                    e.Handled = true;
                }
            };
            lane.Click += (_, _) =>
            {
                if (this.interactive && bar.IsEnabled)
                {
                    this.BarSelected?.Invoke(itemIndex);
                }
            };
            Canvas.SetLeft(lane, index * laneWidth);
            plot.Children.Add(lane);
            this.lanes.Add(lane);
        }

        Grid.SetRow(plot, 1);
        Grid.SetColumn(plot, 1);
        this.Children.Add(plot);

        void RestoreDetail()
        {
            if (this.bars.Count == 0)
            {
                return;
            }

            ShowDetail(
                this.interactive && this.actionLabel is not null
                    ? "Hover or focus a bar to explore"
                    : "Hover or focus a period to inspect its exact value",
                action: false);
            HideInspection();
        }

        plot.MouseLeave += (_, _) =>
        {
            if (!this.IsKeyboardFocusWithin)
            {
                RestoreDetail();
            }
        };
        this.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!this.IsKeyboardFocusWithin && !plot.IsMouseOver)
            {
                RestoreDetail();
            }
        };

        var labels = new Canvas { Width = plotWidth, Height = 32 };
        for (var index = 0; index < this.bars.Count; index++)
        {
            if (this.bars.Count > 12 && index % 3 != 0 && index != this.bars.Count - 1)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = this.bars[index].Label,
                Width = laneWidth,
                FontSize = 11,
                Foreground = this.MutedBrush(),
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(label, index * laneWidth);
            Canvas.SetTop(label, 8);
            labels.Children.Add(label);
        }

        Grid.SetRow(labels, 2);
        Grid.SetColumn(labels, 1);
        this.Children.Add(labels);
    }

    // The base value carries the resting state and the animation only eases toward it. A held
    // reveal animation would otherwise outrank the plain Opacity = 0 of a later hide (mouse
    // leave, Escape) and leave the guide, pill, and marker on screen beside the resting hint.
    private void Fade(UIElement element, double opacity)
    {
        var from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = opacity;
        if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - opacity) < 0.001)
        {
            return;
        }

        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(from, opacity, TimeSpan.FromMilliseconds(83))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
    }

    private TextBlock AddAxisLabel(Canvas axis, string value, double top)
    {
        var label = new TextBlock
        {
            Text = value,
            FontSize = 10.5,
            Foreground = this.MutedBrush(),
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        Canvas.SetLeft(label, 0);
        Canvas.SetTop(label, top);
        axis.Children.Add(label);
        return label;
    }

    /// <summary>Whether an axis label at <paramref name="labelTop"/> overlaps the value pill at <paramref name="pillTop"/>.</summary>
    internal static bool AxisLabelCollides(double labelTop, double pillTop) =>
        labelTop < pillTop + ValuePillHeight && labelTop + AxisLabelHeight > pillTop;

    // WPF hit-tests only what an element draws: a bare ContentPresenter would shrink the lane's
    // pointer target to the visual bar (a 3px stub for an idle period), so the lane paints its
    // own transparent Background across the full plot height and that surface also carries the
    // faint inspect tint.
    private static ControlTemplate TransparentButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        surface.AppendChild(presenter);
        template.VisualTree = surface;
        return template;
    }

    private SolidColorBrush MutedBrush() => new(
        this.isDark ? Color.FromRgb(167, 173, 184) : Color.FromRgb(98, 104, 116));
}
