using CodexWinBar.Widget;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class FullscreenDetectorTests
{
    private const uint DecoratedWindow = NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME;
    private const uint BorderlessMaximizedChrome = 0x15030000;

    [Fact]
    public void IsFullscreenLayout_AcceptsBorderlessMaximizedWindowCoveringMonitor()
    {
        bool fullscreen = FullscreenDetector.IsFullscreenLayout(
            sameMonitor: true,
            zoomed: true,
            coversMonitor: true,
            windowStyle: BorderlessMaximizedChrome);

        Assert.True(fullscreen);
    }

    [Fact]
    public void IsFullscreenLayout_RejectsDecoratedMaximizedWindowCoveringMonitor()
    {
        bool fullscreen = FullscreenDetector.IsFullscreenLayout(
            sameMonitor: true,
            zoomed: true,
            coversMonitor: true,
            windowStyle: DecoratedWindow);

        Assert.False(fullscreen);
    }

    [Fact]
    public void IsFullscreenLayout_AcceptsNonMaximizedWindowCoveringMonitor()
    {
        bool fullscreen = FullscreenDetector.IsFullscreenLayout(
            sameMonitor: true,
            zoomed: false,
            coversMonitor: true,
            windowStyle: DecoratedWindow);

        Assert.True(fullscreen);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsFullscreenLayout_RejectsWrongMonitorOrIncompleteCoverage(bool sameMonitor, bool coversMonitor)
    {
        bool fullscreen = FullscreenDetector.IsFullscreenLayout(
            sameMonitor,
            zoomed: true,
            coversMonitor,
            windowStyle: BorderlessMaximizedChrome);

        Assert.False(fullscreen);
    }

    [Fact]
    public void IsFullscreenLayout_TreatsMissingStyleAsDecoratedForMaximizedWindow()
    {
        bool fullscreen = FullscreenDetector.IsFullscreenLayout(
            sameMonitor: true,
            zoomed: true,
            coversMonitor: true,
            windowStyle: 0);

        Assert.False(fullscreen);
    }
}
