using System.Drawing;
using CodexWinBar.Widget;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class TaskbarStartOccupancyTests
{
    private static readonly Rectangle Taskbar = Rectangle.FromLTRB(0, 1000, 1920, 1048);

    [Fact]
    public void ContiguousStartRight_ReturnsNull_WhenStartIsEmpty()
    {
        int? right = TaskbarStartOccupancy.ContiguousStartRight(Taskbar, 720, []);

        Assert.Null(right);
    }

    [Fact]
    public void ContiguousStartRight_UsesWeatherButtonActualWidth()
    {
        Rectangle weather = Rectangle.FromLTRB(16, 1004, 142, 1044);

        int? right = TaskbarStartOccupancy.ContiguousStartRight(Taskbar, 720, [weather]);

        Assert.Equal(142, right);
    }

    [Fact]
    public void ContiguousStartRight_UsesWholeAdjacentStartBlock()
    {
        Rectangle first = Rectangle.FromLTRB(8, 1004, 96, 1044);
        Rectangle second = Rectangle.FromLTRB(104, 1004, 172, 1044);

        int? right = TaskbarStartOccupancy.ContiguousStartRight(Taskbar, 720, [second, first]);

        Assert.Equal(172, right);
    }

    [Fact]
    public void ContiguousStartRight_IgnoresIsolatedControlNearAppCluster()
    {
        Rectangle isolated = Rectangle.FromLTRB(620, 1004, 680, 1044);

        int? right = TaskbarStartOccupancy.ContiguousStartRight(Taskbar, 720, [isolated]);

        Assert.Null(right);
    }

    [Fact]
    public void ContiguousStartRight_IgnoresControlThatCrossesIntoAppCluster()
    {
        Rectangle crossing = Rectangle.FromLTRB(680, 1004, 760, 1044);

        int? right = TaskbarStartOccupancy.ContiguousStartRight(Taskbar, 720, [crossing]);

        Assert.Null(right);
    }

    [Fact]
    public void ContiguousStartRight_HandlesNegativeSecondaryMonitorCoordinates()
    {
        Rectangle secondaryTaskbar = Rectangle.FromLTRB(-1920, 1080, 0, 1128);
        Rectangle startButton = Rectangle.FromLTRB(-1904, 1084, -1778, 1124);

        int? right = TaskbarStartOccupancy.ContiguousStartRight(secondaryTaskbar, -1200, [startButton]);

        Assert.Equal(-1778, right);
    }

    [Fact]
    public void ContiguousStartRight_IgnoresControlOutsideTaskbarVerticalBand()
    {
        Rectangle unrelated = Rectangle.FromLTRB(8, 900, 120, 950);

        int? right = TaskbarStartOccupancy.ContiguousStartRight(Taskbar, 720, [unrelated]);

        Assert.Null(right);
    }

    [Fact]
    public void DeriveAppCluster_UsesWholeContiguousRunContainingStart()
    {
        (Rectangle Bounds, string AutomationId)[] elements =
        [
            (Rectangle.FromLTRB(16, 1004, 142, 1044), "WidgetsButton"),
            (Rectangle.FromLTRB(720, 1000, 768, 1048), "StartButton"),
            (Rectangle.FromLTRB(768, 1000, 816, 1048), "SearchButton"),
            (Rectangle.FromLTRB(816, 1000, 864, 1048), "TaskViewButton"),
            (Rectangle.FromLTRB(864, 1000, 912, 1048), "Appid: terminal"),
            (Rectangle.FromLTRB(912, 1000, 960, 1048), "Appid: browser"),
            (Rectangle.FromLTRB(1760, 1000, 1808, 1048), "SystemTrayIcon"),
        ];

        Rectangle? cluster = TaskbarStartOccupancy.DeriveAppCluster(Taskbar, elements);

        Assert.Equal(Rectangle.FromLTRB(720, 1000, 960, 1048), cluster);
    }

    [Fact]
    public void DeriveAppCluster_ReturnsNullWithoutAnAppClusterSeed()
    {
        (Rectangle Bounds, string AutomationId)[] elements =
        [
            (Rectangle.FromLTRB(16, 1004, 142, 1044), "WidgetsButton"),
            (Rectangle.FromLTRB(1760, 1000, 1808, 1048), "SystemTrayIcon"),
        ];

        Rectangle? cluster = TaskbarStartOccupancy.DeriveAppCluster(Taskbar, elements);

        Assert.Null(cluster);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(10, 10)]
    [InlineData(12, 12)]
    [InlineData(16, 16)]
    public void StartInset_UsesDpiScaledPadding_WhenStartIsEmpty(int padding, int expected)
    {
        Assert.Equal(expected, WidgetWindow.StartInset(Taskbar, null, padding));
    }

    [Fact]
    public void StartInset_HandlesOccupiedContentOnNegativeMonitor()
    {
        Rectangle secondaryTaskbar = Rectangle.FromLTRB(-1920, 1080, 0, 1128);

        int inset = WidgetWindow.StartInset(secondaryTaskbar, -1778, 10);

        Assert.Equal(152, inset);
    }

    [Theory]
    [InlineData(600, 536, true, true, true)]
    [InlineData(2, 153, true, true, false)]
    [InlineData(600, 536, false, true, false)]
    [InlineData(600, 536, false, false, true)]
    public void HasHorizontalRoom_RejectsOverlapAndUnknownStartGeometry(
        int budget,
        int measuredWidth,
        bool startLayoutKnown,
        bool anchorStart,
        bool expected)
    {
        Assert.Equal(expected, WidgetWindow.HasHorizontalRoom(budget, measuredWidth, startLayoutKnown, anchorStart));
    }

    [Fact]
    public void SameHorizontalLayoutSource_RejectsMovedTaskbarWithSameHandleAndDpi()
    {
        IntPtr taskbar = new(123);
        Rectangle moved = new(Taskbar.X - 1920, Taskbar.Y + 80, Taskbar.Width, Taskbar.Height);

        bool same = WidgetWindow.SameHorizontalLayoutSource(taskbar, 144, Taskbar, taskbar, 144, moved);

        Assert.False(same);
    }

    [Fact]
    public void SameHorizontalLayoutSource_AcceptsIdenticalTaskbarGeometry()
    {
        IntPtr taskbar = new(123);

        bool same = WidgetWindow.SameHorizontalLayoutSource(taskbar, 144, Taskbar, taskbar, 144, Taskbar);

        Assert.True(same);
    }
}
