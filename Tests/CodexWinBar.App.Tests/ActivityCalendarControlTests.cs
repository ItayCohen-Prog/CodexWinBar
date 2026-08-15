using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CodexWinBar.App.Statistics;
using CodexWinBar.Core.Statistics;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class ActivityCalendarControlTests
{
    [Fact]
    public void Same_calendar_month_accepts_different_days()
    {
        Assert.True(ActivityCalendarControl.IsSameCalendarMonth(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 31)));
    }

    [Theory]
    [InlineData(2026, 8, 31, 2026, 9, 1)]
    [InlineData(2026, 8, 15, 2027, 8, 15)]
    public void Same_calendar_month_rejects_another_month_or_year(
        int leftYear,
        int leftMonth,
        int leftDay,
        int rightYear,
        int rightMonth,
        int rightDay)
    {
        Assert.False(ActivityCalendarControl.IsSameCalendarMonth(
            new DateOnly(leftYear, leftMonth, leftDay),
            new DateOnly(rightYear, rightMonth, rightDay)));
    }

    [Fact]
    public void Full_intensity_preserves_the_provider_ink()
    {
        var claudeOrange = Color.FromRgb(217, 119, 87);

        var color = ActivityCalendarControl.IntensityColor(1, claudeOrange);

        Assert.Equal(claudeOrange.R, color.R);
        Assert.Equal(claudeOrange.G, color.G);
        Assert.Equal(claudeOrange.B, color.B);
        Assert.Equal(byte.MaxValue, color.A);
    }

    [Fact]
    public void Keyboard_focus_and_arrow_travel_show_the_month_outline_and_cue()
    {
        RunOnStaThread(() =>
        {
            var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
            var activity = PlanStatisticsProjection.BuildActivity(
                new PlanUsageSeries { Id = "weekly", Title = "Weekly", WindowMinutes = 10080 },
                now,
                2026);
            var control = new ActivityCalendarControl(
                activity.Days,
                new DateOnly(2026, 8, 11),
                activity.StartsOn,
                activity.EndsOn,
                Color.FromRgb(18, 20, 23),
                isDark: false,
                value => $"{value}%");
            var cues = new List<bool>();
            control.MonthHoverChanged += cues.Add;

            // Tabbing in (no pointer over the calendar) outlines the selected month right away.
            control.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, null)
            {
                RoutedEvent = Keyboard.GotKeyboardFocusEvent,
            });
            Assert.Equal([true], cues);
            Assert.Equal(new DateOnly(2026, 8, 11), HoverDate(control));
            Assert.True(OutlineIsShowing(control));

            // Left moves one week back and keeps the outline (and the header cue) alive.
            using var source = new HwndSource(new HwndSourceParameters("calendar-key-test")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            });
            control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Left)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
            Assert.Equal(new DateOnly(2026, 8, 4), HoverDate(control));
            Assert.True(OutlineIsShowing(control));
        });
    }

    private static DateOnly? HoverDate(ActivityCalendarControl control) =>
        (DateOnly?)typeof(ActivityCalendarControl)
            .GetField("hoverDate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(control);

    // The outline pen alpha follows HoverOpacity: either an animation toward 1 is running or,
    // with client-area animation disabled, the value already sits at 1.
    private static bool OutlineIsShowing(ActivityCalendarControl control)
    {
        var property = (DependencyProperty)typeof(ActivityCalendarControl)
            .GetField("HoverOpacityProperty", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        return control.HasAnimatedProperties || (double)control.GetValue(property) == 1;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF calendar test did not finish.");
        Assert.Null(failure);
    }
}
