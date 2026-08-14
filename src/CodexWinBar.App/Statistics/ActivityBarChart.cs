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
        AutomationProperties.SetHelpText(this, "Use Left and Right to inspect periods. Press Enter to open the selected period.");
        this.Build();
    }

    internal event Action<int>? BarSelected;

    internal event Action<int>? BarInspected;

    private void Build()
    {
        var laneWidth = this.bars.Count > 12 ? 30d : 64d;
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
            Text = this.bars.Count == 0 ? "No observed activity" : "Hover or focus a period to inspect its exact value",
            Margin = new Thickness(0, 0, 0, 7),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.MutedBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(detail, FontNumeralAlignment.Tabular);
        AutomationProperties.SetLiveSetting(detail, AutomationLiveSetting.Polite);
        Grid.SetColumnSpan(detail, 2);
        this.Children.Add(detail);

        var axis = new Canvas { Width = AxisWidth, Height = PlotHeight };
        this.AddAxisLabel(axis, this.valueFormatter(max), 0);
        this.AddAxisLabel(axis, this.valueFormatter(max / 2), (PlotHeight / 2) - 8);
        this.AddAxisLabel(axis, this.valueFormatter(0), PlotHeight - 18);
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

        var laneBrushes = new List<SolidColorBrush>();
        var barBrushes = new List<SolidColorBrush>();
        for (var index = 0; index < this.bars.Count; index++)
        {
            var itemIndex = index;
            var bar = this.bars[index];
            var height = bar.Value <= 0 ? 3 : Math.Max(5, (PlotHeight - 8) * bar.Value / max);
            var laneBrush = new SolidColorBrush(Colors.Transparent);
            var barBrush = new SolidColorBrush(Color.FromArgb(205, this.accent.R, this.accent.G, this.accent.B));
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
            };
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
                    barBrushes[laneIndex].Color = Color.FromArgb(205, this.accent.R, this.accent.G, this.accent.B);
                }

                laneBrush.Color = Color.FromArgb(14, this.accent.R, this.accent.G, this.accent.B);
                barBrush.Color = Color.FromArgb(244, this.accent.R, this.accent.G, this.accent.B);
                detail.Text = bar.Description;
                valueText.Text = this.valueFormatter(bar.Value);
                var y = Math.Max(0, PlotHeight - height);
                var barNearEdge = (itemIndex * laneWidth) + ((laneWidth - visualBarWidth) / 2);
                guide.X1 = 0;
                guide.X2 = Math.Max(0, barNearEdge - 4);
                guide.Y1 = y;
                guide.Y2 = y;
                Canvas.SetTop(valuePill, Math.Clamp(y - 11, 0, PlotHeight - 25));
                this.Reveal(valuePill);
                this.Reveal(guide);
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
                        guide.Opacity = 0;
                        valuePill.Opacity = 0;
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

    private void Reveal(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1;
            return;
        }

        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(element.Opacity, 1, TimeSpan.FromMilliseconds(83))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void AddAxisLabel(Canvas axis, string value, double top)
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
    }

    private static ControlTemplate TransparentButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        template.VisualTree = presenter;
        return template;
    }

    private SolidColorBrush MutedBrush() => new(
        this.isDark ? Color.FromRgb(167, 173, 184) : Color.FromRgb(98, 104, 116));
}
