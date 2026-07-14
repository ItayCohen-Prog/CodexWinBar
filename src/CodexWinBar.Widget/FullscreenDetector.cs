namespace CodexWinBar.Widget;

/// <summary>Full breakdown of the fullscreen check on a monitor, so diagnostics can see WHY it fired.</summary>
internal readonly record struct FullscreenInfo(
    bool IsFullscreen,
    IntPtr Foreground,
    bool SameMonitor,
    bool Zoomed,
    bool CoversMonitor,
    NativeMethods.RECT ForegroundRect);

internal static class FullscreenDetector
{
    /// <summary>
    /// True when the foreground window is a genuine fullscreen app on <paramref name="monitor"/> — a
    /// borderless/exclusive window covering the whole monitor that is NOT merely maximized. Per-monitor by
    /// design: each widget passes ITS OWN taskbar's monitor. <paramref name="ignoreWindow"/> (the widget
    /// itself) never counts.
    /// </summary>
    internal static bool IsForegroundFullscreenOnMonitor(IntPtr monitor, IntPtr ignoreWindow) =>
        Describe(monitor, ignoreWindow).IsFullscreen;

    /// <summary>As <see cref="IsForegroundFullscreenOnMonitor"/>, but returns every intermediate factor.</summary>
    internal static FullscreenInfo Describe(IntPtr monitor, IntPtr ignoreWindow)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ignoreWindow || monitor == IntPtr.Zero)
        {
            return new FullscreenInfo(false, foreground, false, false, false, default);
        }

        IntPtr foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        bool sameMonitor = foregroundMonitor == monitor;
        bool zoomed = NativeMethods.IsZoomed(foreground);

        bool coversMonitor = false;
        NativeMethods.RECT rect = default;
        if (sameMonitor)
        {
            NativeMethods.MONITORINFO info = new()
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (NativeMethods.GetMonitorInfoW(foregroundMonitor, ref info) && NativeMethods.GetWindowRect(foreground, out rect))
            {
                coversMonitor = rect.Left <= info.rcMonitor.Left
                    && rect.Top <= info.rcMonitor.Top
                    && rect.Right >= info.rcMonitor.Right
                    && rect.Bottom >= info.rcMonitor.Bottom;
            }
        }

        // A normal MAXIMIZED window is not fullscreen: it leaves the taskbar usable, so the widget must
        // stay. Only a genuine borderless/exclusive fullscreen app (covers the monitor, not maximized)
        // hides the widget.
        bool isFullscreen = sameMonitor && !zoomed && coversMonitor;
        return new FullscreenInfo(isFullscreen, foreground, sameMonitor, zoomed, coversMonitor, rect);
    }
}
