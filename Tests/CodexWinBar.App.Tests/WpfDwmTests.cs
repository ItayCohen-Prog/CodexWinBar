using CodexWinBar.App.Interop;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class WpfDwmTests
{
    [Fact]
    public void ClampWindowOrigin_moves_caption_below_monitor_top()
    {
        var window = new WpfDwm.RectInt(-2610, 243, -1050, 1473);
        var workArea = new WpfDwm.RectInt(-2880, 392, 0, 1940);

        var result = WpfDwm.ClampWindowOrigin(window, workArea);

        Assert.Equal(-2610, result.X);
        Assert.Equal(392, result.Y);
    }

    [Fact]
    public void ClampWindowOrigin_preserves_visible_window_position()
    {
        var window = new WpfDwm.RectInt(200, 160, 1240, 980);
        var workArea = new WpfDwm.RectInt(0, 0, 2560, 1528);

        var result = WpfDwm.ClampWindowOrigin(window, workArea);

        Assert.Equal(200, result.X);
        Assert.Equal(160, result.Y);
    }
}
