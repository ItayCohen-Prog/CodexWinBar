namespace CodexWinBar.Widget;

internal static class FullscreenDetector
{
    internal static bool IsForegroundFullscreenOnMonitor(IntPtr referenceWindow)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == referenceWindow)
        {
            return false;
        }

        IntPtr referenceMonitor = NativeMethods.MonitorFromWindow(referenceWindow, NativeMethods.MONITOR_DEFAULTTONEAREST);
        IntPtr foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (referenceMonitor == IntPtr.Zero || foregroundMonitor == IntPtr.Zero || referenceMonitor != foregroundMonitor)
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
