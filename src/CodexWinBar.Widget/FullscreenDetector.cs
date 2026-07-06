namespace CodexWinBar.Widget;

internal static class FullscreenDetector
{
    /// <summary>
    /// True when the foreground window covers all of <paramref name="monitor"/>. Per-monitor by design:
    /// each widget passes ITS OWN taskbar's monitor, so a fullscreen app on one display only hides the
    /// widget on that display. <paramref name="ignoreWindow"/> (the widget itself) never counts.
    /// </summary>
    internal static bool IsForegroundFullscreenOnMonitor(IntPtr monitor, IntPtr ignoreWindow)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ignoreWindow || monitor == IntPtr.Zero)
        {
            return false;
        }

        IntPtr foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (foregroundMonitor != monitor)
        {
            return false;
        }

        NativeMethods.MONITORINFO info = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfoW(foregroundMonitor, ref info) || !NativeMethods.GetWindowRect(foreground, out NativeMethods.RECT rect))
        {
            return false;
        }

        return rect.Left <= info.rcMonitor.Left
            && rect.Top <= info.rcMonitor.Top
            && rect.Right >= info.rcMonitor.Right
            && rect.Bottom >= info.rcMonitor.Bottom;
    }
}
