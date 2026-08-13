using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CodexWinBar.App.Statistics;

internal sealed record ActivityBar(string Label, double Value, string Description, bool IsEnabled = true);

/// <summary>An accessible bar chart built from focusable WPF buttons.</summary>
internal sealed class ActivityBarChart : Grid
{
    private static readonly CultureInfo UiCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly IReadOnlyList<ActivityBar> bars;
    private readonly Color accent;
    private readonly bool isDark;
    private readonly bool interactive;
    private readonly string? actionLabel;

    internal ActivityBarChart(
        IReadOnlyList<ActivityBar> bars,
        Color accent,
        bool isDark,
        bool interactive,
        string? actionLabel = null)
    {
        this.bars = bars;
        this.accent = accent;
        this.isDark = isDark;
        this.interactive = interactive;
        this.actionLabel = actionLabel;
        this.MinHeight = 200;
        AutomationProperties.SetName(this, "Activity bar chart");
        this.Build();
    }

    internal event Action<int>? BarSelected;

    private void Build()
    {
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        this.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        this.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var laneWidth = this.bars.Count > 12 ? 30d : 56d;
        var plotWidth = Math.Max(laneWidth, this.bars.Count * laneWidth);
        this.Width = 60 + plotWidth;
        this.HorizontalAlignment = HorizontalAlignment.Left;

        var max = Math.Max(1, this.bars.Count == 0 ? 0 : this.bars.Max(bar => bar.Value));
        var labels = new Grid();
        labels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        labels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        labels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        this.AddAxisLabel(labels, 0, $"{max:0.#}", VerticalAlignment.Top);
        this.AddAxisLabel(labels, 1, $"{max / 2:0.#}", VerticalAlignment.Center);
        this.AddAxisLabel(labels, 2, "0", VerticalAlignment.Bottom);
        var hoverValueText = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(this.isDark ? Colors.White : Color.FromRgb(23, 25, 29)),
        };
        var hoverValue = new Border
        {
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(this.isDark ? Color.FromRgb(35, 39, 47) : Colors.White),
            BorderBrush = new SolidColorBrush(this.accent),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = hoverValueText,
        };
        Grid.SetRowSpan(hoverValue, 3);
        Panel.SetZIndex(hoverValue, 3);
        labels.Children.Add(hoverValue);
        this.Children.Add(labels);

        var plot = new Grid
        {
            Width = plotWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        for (var index = 0; index < this.bars.Count; index++)
        {
            plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (var alignment in new[] { VerticalAlignment.Top, VerticalAlignment.Center, VerticalAlignment.Bottom })
        {
            plot.Children.Add(new Border
            {
                Height = 1,
                VerticalAlignment = alignment,
                Background = new SolidColorBrush(this.isDark
                    ? Color.FromArgb(50, 255, 255, 255)
                    : Color.FromArgb(30, 23, 25, 29)),
            });
        }

        var hoverGuide = new Line
        {
            X1 = 0,
            Y1 = 0,
            X2 = 1,
            Y2 = 0,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0,
            IsHitTestVisible = false,
            Stretch = Stretch.Fill,
            Stroke = new SolidColorBrush(this.isDark
                ? Color.FromArgb(145, 183, 188, 198)
                : Color.FromArgb(120, 88, 96, 108)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeDashCap = PenLineCap.Flat,
        };
        Panel.SetZIndex(hoverGuide, 1);
        plot.Children.Add(hoverGuide);

        for (var index = 0; index < this.bars.Count; index++)
        {
            var bar = this.bars[index];
            var slotBrush = new SolidColorBrush(Color.FromArgb(0, this.accent.R, this.accent.G, this.accent.B));
            var slot = new Grid
            {
                Margin = new Thickness(3, 0, 3, 0),
                Background = slotBrush,
                Cursor = this.interactive && bar.IsEnabled ? Cursors.Hand : Cursors.Arrow,
                ToolTip = bar.Description,
            };
            ToolTipService.SetInitialShowDelay(slot, 160);
            ToolTipService.SetBetweenShowDelay(slot, 0);
            var barBrush = new SolidColorBrush(Color.FromArgb(205, this.accent.R, this.accent.G, this.accent.B));
            var button = new Button
            {
                Height = bar.Value <= 0 ? 3 : Math.Max(5, 154 * bar.Value / max),
                Width = this.bars.Count > 12 ? 12 : 30,
                MaxWidth = 30,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = barBrush,
                BorderBrush = new SolidColorBrush(this.accent),
                BorderThickness = new Thickness(1),
                Cursor = this.interactive && bar.IsEnabled ? Cursors.Hand : Cursors.Arrow,
                IsEnabled = !this.interactive || bar.IsEnabled,
                Tag = index,
            };
            void ShowInspection()
            {
                var barHeight = button.ActualHeight > 0 ? button.ActualHeight : button.Height;
                var barEdge = button.TranslatePoint(new Point(0, 0), plot).X;
                var duration = TimeSpan.FromMilliseconds(120);
                var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var currentWidth = double.IsNaN(hoverGuide.Width) ? hoverGuide.ActualWidth : hoverGuide.Width;
                hoverGuide.BeginAnimation(
                    WidthProperty,
                    new DoubleAnimation(currentWidth, Math.Max(0, barEdge), duration) { EasingFunction = easing });
                hoverGuide.BeginAnimation(
                    MarginProperty,
                    new ThicknessAnimation(hoverGuide.Margin, new Thickness(0, 0, 0, barHeight), duration) { EasingFunction = easing });
                hoverGuide.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(hoverGuide.Opacity, 1, duration) { EasingFunction = easing });
                hoverValueText.Text = $"{bar.Value.ToString("0.#", UiCulture)} pts";
                hoverValue.BeginAnimation(
                    MarginProperty,
                    new ThicknessAnimation(
                        hoverValue.Margin,
                        new Thickness(0, 0, 4, Math.Max(0, barHeight - 10)),
                        duration)
                    {
                        EasingFunction = easing,
                    });
                hoverValue.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(hoverValue.Opacity, 1, duration) { EasingFunction = easing });
                slotBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromArgb(10, this.accent.R, this.accent.G, this.accent.B), duration));
                barBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromArgb(245, this.accent.R, this.accent.G, this.accent.B), duration));
            }

            void HideInspection()
            {
                var duration = TimeSpan.FromMilliseconds(100);
                var easing = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                hoverGuide.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(hoverGuide.Opacity, 0, duration) { EasingFunction = easing });
                hoverValue.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(hoverValue.Opacity, 0, duration) { EasingFunction = easing });
                slotBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromArgb(0, this.accent.R, this.accent.G, this.accent.B), duration));
                barBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromArgb(205, this.accent.R, this.accent.G, this.accent.B), duration));
            }

            slot.MouseEnter += (_, _) => ShowInspection();
            slot.MouseLeave += (_, _) =>
            {
                if (!button.IsKeyboardFocused)
                {
                    HideInspection();
                }
            };
            button.GotKeyboardFocus += (_, _) => ShowInspection();
            button.LostKeyboardFocus += (_, _) =>
            {
                if (!slot.IsMouseOver)
                {
                    HideInspection();
                }
            };
            slot.MouseLeftButtonUp += (_, e) =>
            {
                if (this.interactive && bar.IsEnabled && !button.IsMouseOver)
                {
                    this.BarSelected?.Invoke((int)button.Tag);
                    e.Handled = true;
                }
            };
            button.Click += (_, _) =>
            {
                if (this.interactive && bar.IsEnabled)
                {
                    this.BarSelected?.Invoke((int)button.Tag);
                }
            };
            AutomationProperties.SetName(
                button,
                this.interactive && this.actionLabel is not null
                    ? $"{bar.Description}. {this.actionLabel}."
                    : bar.Description);
            slot.Children.Add(button);
            Panel.SetZIndex(slot, 2);
            Grid.SetColumn(slot, index);
            plot.Children.Add(slot);
        }

        Grid.SetColumn(plot, 1);
        this.Children.Add(plot);

        var barLabels = new Grid
        {
            Width = plotWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        for (var index = 0; index < this.bars.Count; index++)
        {
            barLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (this.bars.Count > 8 && index % 3 != 0)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = this.bars[index].Label,
                FontSize = 10.5,
                Foreground = this.MutedBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, index);
            barLabels.Children.Add(label);
        }

        Grid.SetRow(barLabels, 1);
        Grid.SetColumn(barLabels, 1);
        this.Children.Add(barLabels);
    }

    private void AddAxisLabel(Grid labels, int row, string value, VerticalAlignment alignment)
    {
        var label = new TextBlock
        {
            Text = value,
            FontSize = 10.5,
            Foreground = this.MutedBrush(),
            VerticalAlignment = alignment,
        };
        Grid.SetRow(label, row);
        labels.Children.Add(label);
    }

    private SolidColorBrush MutedBrush() => new(
        this.isDark ? Color.FromRgb(167, 173, 184) : Color.FromRgb(98, 104, 116));
}
