using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexWinBar.App.Statistics;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class ActivityBarChartTests
{
    [Fact]
    public void Hourly_chart_keeps_full_height_lanes_thinned_labels_and_honest_help_text()
    {
        RunOnStaThread(() =>
        {
            var bars = Enumerable.Range(0, 24)
                .Select(hour => new ActivityBar($"{hour:00}", hour, $"{hour:00}:00 description"))
                .ToArray();
            var chart = new ActivityBarChart(
                bars,
                Color.FromRgb(30, 63, 69),
                isDark: false,
                interactive: false,
                valueFormatter: value => $"{value}% used");

            var plot = PlotCanvas(chart);
            var lanes = plot.Children.OfType<Button>().ToArray();
            Assert.Equal(24, lanes.Length);
            Assert.All(lanes, lane =>
            {
                Assert.Equal(28, lane.Width);
                Assert.Equal(176, lane.Height);
                Assert.False(string.IsNullOrEmpty((string?)lane.ToolTip));
            });

            // Axis plus 24 lanes must clear the 900px minimum window beside its scrollbar.
            Assert.True(chart.Width <= 800, $"hourly chart width {chart.Width} overflows the minimum window");

            // One point marker, hidden until a lane is inspected.
            var marker = Assert.Single(plot.Children.OfType<Ellipse>());
            Assert.Equal(0, marker.Opacity);

            // Every third hour plus the final one keeps a label; the rest are thinned out.
            var labels = LabelsCanvas(chart).Children.OfType<TextBlock>().ToArray();
            Assert.Equal(9, labels.Length);

            // A chart without drill-down must not promise an Enter action.
            Assert.DoesNotContain("Enter", AutomationProperties.GetHelpText(chart));
        });
    }

    [Fact]
    public void Inspecting_a_lane_places_the_marker_guide_and_description()
    {
        RunOnStaThread(() =>
        {
            var values = new[] { 10d, 20d, 40d, 30d, 5d };
            var bars = values
                .Select((value, index) => new ActivityBar($"Day {index}", value, $"Day {index}: {value}% used"))
                .ToArray();
            var chart = new ActivityBarChart(
                bars,
                Color.FromRgb(217, 119, 87),
                isDark: false,
                interactive: true,
                valueFormatter: value => $"{value}% used",
                actionLabel: "View day details");

            Assert.Contains("Enter", AutomationProperties.GetHelpText(chart));

            var plot = PlotCanvas(chart);
            var lane = plot.Children.OfType<Button>().ElementAt(2);
            Assert.Null(lane.ToolTip);
            lane.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, null)
            {
                RoutedEvent = Keyboard.GotKeyboardFocusEvent,
            });

            // The maximum bar fills the plot: height 168 inside the 176px plot, so its top sits at y=8.
            var marker = Assert.Single(plot.Children.OfType<Ellipse>());
            Assert.Equal((2 * 56) + 28 - 3.5, Canvas.GetLeft(marker));
            Assert.Equal(8 - 3.5, Canvas.GetTop(marker));

            var guide = plot.Children.OfType<Line>().Single(line => line.StrokeDashArray.Count > 0);
            Assert.Equal(0, guide.X1);
            Assert.Equal((2 * 56) + ((56 - 32) / 2d) - 4, guide.X2);
            Assert.Equal(8, guide.Y1);
            Assert.Equal(8, guide.Y2);

            var detail = chart.Children.OfType<TextBlock>().First();
            Assert.Equal("View day details  →", detail.Text);
        });
    }

    [Fact]
    public void Value_pill_hides_only_the_axis_label_it_would_cover()
    {
        RunOnStaThread(() =>
        {
            // 37 of 40 puts the pill just under the top label; 20 of 40 puts it on the middle one.
            var values = new[] { 10d, 20d, 40d, 37d, 5d };
            var bars = values
                .Select((value, index) => new ActivityBar($"Day {index}", value, $"Day {index}: {value}% used"))
                .ToArray();
            var chart = new ActivityBarChart(
                bars,
                Color.FromRgb(217, 119, 87),
                isDark: false,
                interactive: true,
                valueFormatter: value => $"{value}% used",
                actionLabel: "View day details");
            var lanes = PlotCanvas(chart).Children.OfType<Button>().ToArray();
            var labels = AxisCanvas(chart).Children.OfType<TextBlock>().OrderBy(Canvas.GetTop).ToArray();
            Assert.Equal(3, labels.Length);

            Focus(lanes[3]);
            Assert.Equal(0, labels[0].Opacity);
            Assert.Equal(1, labels[1].Opacity);
            Assert.Equal(1, labels[2].Opacity);

            Focus(lanes[1]);
            Assert.Equal(1, labels[0].Opacity);
            Assert.Equal(0, labels[1].Opacity);
            Assert.Equal(1, labels[2].Opacity);

            // Leaving the plot with no keyboard focus inside restores the resting axis.
            PlotCanvas(chart).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = Mouse.MouseLeaveEvent,
            });
            Assert.All(labels, label => Assert.Equal(1, label.Opacity));
        });
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 9.6, true)]
    [InlineData(0, 15, false)]
    [InlineData(80, 60, true)]
    [InlineData(80, 55, false)]
    [InlineData(158, 151, true)]
    public void Axis_label_collision_uses_the_pill_and_label_extents(double labelTop, double pillTop, bool collides)
    {
        Assert.Equal(collides, ActivityBarChart.AxisLabelCollides(labelTop, pillTop));
    }

    [Fact]
    public void Leaving_the_plot_hides_the_guide_pill_marker_and_lane_tint()
    {
        RunOnStaThread(() =>
        {
            var values = new[] { 10d, 20d, 40d, 30d, 5d };
            var bars = values
                .Select((value, index) => new ActivityBar($"Day {index}", value, $"Day {index}: {value}% used"))
                .ToArray();
            var chart = new ActivityBarChart(
                bars,
                Color.FromRgb(217, 119, 87),
                isDark: false,
                interactive: true,
                valueFormatter: value => $"{value}% used",
                actionLabel: "View day details");
            var plot = PlotCanvas(chart);
            var lanes = plot.Children.OfType<Button>().ToArray();
            var guide = plot.Children.OfType<Line>().Single(line => line.StrokeDashArray.Count > 0);
            var marker = Assert.Single(plot.Children.OfType<Ellipse>());
            var pill = AxisCanvas(chart).Children.OfType<Border>().Single();
            var detail = chart.Children.OfType<TextBlock>().First();
            var laneBrush = (SolidColorBrush)lanes[2].Background;
            var barBrush = (SolidColorBrush)((Border)lanes[2].Content).Background;

            Focus(lanes[2]);
            Assert.Equal(1, BaseOpacity(guide));
            Assert.Equal(1, BaseOpacity(pill));
            Assert.Equal(1, BaseOpacity(marker));
            Assert.Equal(14, laneBrush.Color.A);
            Assert.Equal(244, barBrush.Color.A);

            // The pointer leaves with no keyboard focus inside: every inspection cue must go, so
            // the resting hint never sits beside a stale value pill.
            plot.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Equal("Hover or focus a bar to explore", detail.Text);
            Assert.Equal(0, BaseOpacity(guide));
            Assert.Equal(0, BaseOpacity(pill));
            Assert.Equal(0, BaseOpacity(marker));
            Assert.Equal(0, laneBrush.Color.A);
            Assert.Equal(205, barBrush.Color.A);

            // The hide fades toward a base value of 0 with FillBehavior.Stop, so once the clock
            // runs out nothing holds the cues at full opacity beside the resting hint.
            foreach (var cue in new UIElement[] { guide, pill, marker })
            {
                Assert.Equal(0, cue.Opacity);
            }
        });
    }

    [Fact]
    public void Lane_pointer_target_covers_the_full_plot_height_even_for_idle_periods()
    {
        RunOnStaThread(() =>
        {
            var values = new[] { 0d, 10d, 40d, 100d, 5d, 0d, 0d };
            var bars = values
                .Select((value, index) => new ActivityBar($"Day {index}", value, $"Day {index}: {value}% used"))
                .ToArray();
            var chart = new ActivityBarChart(
                bars,
                Color.FromRgb(217, 119, 87),
                isDark: false,
                interactive: true,
                valueFormatter: value => $"{value}% used",
                actionLabel: "View day details");
            chart.Measure(new Size(800, 400));
            chart.Arrange(new Rect(0, 0, 800, 400));
            chart.UpdateLayout();
            var plot = PlotCanvas(chart);

            // Idle and low periods draw only a stub; the pointer must still reach them anywhere in
            // the lane, otherwise "hover a bar to explore" is impossible for the quiet days.
            Assert.Equal("Day 0", LaneAt(plot, new Point(28, 20)));
            Assert.Equal("Day 0", LaneAt(plot, new Point(28, 174)));
            Assert.Equal("Day 1", LaneAt(plot, new Point(56 + 28, 20)));
            Assert.Equal("Day 4", LaneAt(plot, new Point((4 * 56) + 28, 88)));
            Assert.Equal("Day 3", LaneAt(plot, new Point((3 * 56) + 28, 20)));
        });
    }

    // Reveal/hide animate toward the base value; the base value is the intended resting state.
    private static double BaseOpacity(UIElement element) =>
        (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);

    private static string? LaneAt(Canvas plot, Point point)
    {
        DependencyObject? visual = VisualTreeHelper.HitTest(plot, point)?.VisualHit;
        while (visual is not null)
        {
            if (visual is Button lane)
            {
                return AutomationProperties.GetName(lane).Split(':')[0];
            }

            visual = VisualTreeHelper.GetParent(visual);
        }

        return null;
    }

    private static void Focus(Button lane) => lane.RaiseEvent(
        new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, null)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent,
        });

    private static Canvas AxisCanvas(ActivityBarChart chart) => chart.Children.OfType<Canvas>()
        .Single(canvas => Grid.GetRow(canvas) == 1 && Grid.GetColumn(canvas) == 0);

    private static Canvas PlotCanvas(ActivityBarChart chart) => chart.Children.OfType<Canvas>()
        .Single(canvas => Grid.GetRow(canvas) == 1 && Grid.GetColumn(canvas) == 1);

    private static Canvas LabelsCanvas(ActivityBarChart chart) => chart.Children.OfType<Canvas>()
        .Single(canvas => Grid.GetRow(canvas) == 2);

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF chart test did not finish.");
        Assert.Null(failure);
    }
}
