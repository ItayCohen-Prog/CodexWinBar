using System.Diagnostics;
using System.Drawing;

namespace CodexWinBar.Widget;

internal sealed record TaskbarInfo(IntPtr TaskbarHwnd, IntPtr TrayHwnd, Rectangle TaskbarRect, Rectangle ClientRect, Rectangle TrayRect, int Edge, bool IsRtl, bool IsExplorer);

internal static class TaskbarInterop
{
    internal static TaskbarInfo? TryGetPrimaryTaskbar()
    {
        IntPtr taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return null;
        }

        IntPtr tray = FindDescendant(taskbar, "TrayNotifyWnd");
        _ = NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT taskbarRect);
        _ = NativeMethods.GetClientRect(taskbar, out NativeMethods.RECT clientRect);
        NativeMethods.RECT trayRect = default;
        if (tray != IntPtr.Zero)
        {
            _ = NativeMethods.GetWindowRect(tray, out trayRect);
        }

        long exStyle = NativeMethods.GetWindowLongPtrW(taskbar, NativeMethods.GWL_EXSTYLE).ToInt64();
        int edge = GetTaskbarEdge();

        return new TaskbarInfo(
            taskbar,
            tray,
            ToRectangle(taskbarRect),
            ToRectangle(clientRect),
            ToRectangle(trayRect),
            edge,
            (exStyle & NativeMethods.WS_EX_LAYOUTRTL) != 0,
            IsExplorerProcess(taskbar));
    }

    internal static bool IsAutoHidden()
    {
        NativeMethods.APPBARDATA data = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };
        nint state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref data);
        return ((int)state & NativeMethods.ABS_AUTOHIDE) != 0;
    }

    internal static int GetTaskbarEdge()
    {
        NativeMethods.APPBARDATA data = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };
        nint result = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data);
        return result == IntPtr.Zero ? NativeMethods.ABE_BOTTOM : (int)data.uEdge;
    }

    internal static bool IsExplorerProcess(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool RectIntersectsTaskbarClient(IntPtr taskbar, Rectangle screenRect)
    {
        if (!NativeMethods.GetClientRect(taskbar, out NativeMethods.RECT client))
        {
            return false;
        }

        NativeMethods.RECT mapped = client;
        _ = NativeMethods.MapWindowPoints(taskbar, IntPtr.Zero, ref mapped, 2);
        return ToRectangle(mapped).IntersectsWith(screenRect);
    }

    internal static Rectangle ToRectangle(NativeMethods.RECT rect) => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static IntPtr FindDescendant(IntPtr parent, string className)
    {
        IntPtr direct = NativeMethods.FindWindowExW(parent, IntPtr.Zero, className, null);
        if (direct != IntPtr.Zero)
        {
            return direct;
        }

        IntPtr child = IntPtr.Zero;
        while ((child = NativeMethods.FindWindowExW(parent, child, null, null)) != IntPtr.Zero)
        {
            IntPtr found = FindDescendant(child, className);
            if (found != IntPtr.Zero)
            {
                return found;
            }
        }

        return IntPtr.Zero;
    }
}
