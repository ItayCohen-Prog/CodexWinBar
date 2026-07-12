using CodexWinBar.App.Flyout;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class FlyoutWindowGeometryTests
{
    [Theory]
    [InlineData(1.0, 380)]
    [InlineData(2.0 / 3.0, 570)]
    public void IntendedWindowWidthPx_ScalesFromImmutableDesignWidth(double deviceToDipX, int expectedWidthPx)
    {
        Assert.Equal(expectedWidthPx, FlyoutWindow.IntendedWindowWidthPx(deviceToDipX));
    }
}
