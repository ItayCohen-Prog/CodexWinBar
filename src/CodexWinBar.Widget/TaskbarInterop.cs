using System.Diagnostics;
using System.Drawing;

namespace CodexWinBar.Widget;

/// <summary>Snapshot of one taskbar (the primary Shell_TrayWnd or a secondary Shell_SecondaryTrayWnd).
/// <paramref name="Monitor"/> is the HMONITOR the taskbar lives on and is the stable identity widgets
/// are keyed by; <paramref name="TrayHwnd"/>/<paramref name="TrayRect"/> are empty on secondary
/// taskbars, which have no notification area.</summary>
internal sealed record TaskbarInfo(IntPtr TaskbarHwnd, IntPtr TrayHwnd, IntPtr Monitor, bool IsPrimary, Rectangle TaskbarRect, Rectangle ClientRect, Rectangle TrayRect, int Edge, bool IsRtl, bool IsExplorer);

internal static class TaskbarInterop
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>Snapshot of the primary taskbar (Shell_TrayWnd), or null when Explorer is not running.</summary>
    internal static TaskbarInfo? TryGetPrimaryTaskbar()
    {
        IntPtr taskbar = NativeMethods.FindWindowW(PrimaryTaskbarClass, null);
        return taskbar == IntPtr.Zero ? null : BuildInfo(taskbar, isPrimary: true);
    }

    /// <summary>
    /// Snapshots every SECONDARY taskbar currently present: one Shell_SecondaryTrayWnd per additional
    /// display when "show taskbar on all displays" is enabled. The primary Shell_TrayWnd is handled
    /// separately (there is always exactly one) via <see cref="TryGetPrimaryTaskbar"/>, so it is never
    /// keyed by its volatile monitor handle. Secondary taskbars are enumerated with a
    /// FindWindowExW(NULL, …) top-level walk by class name (equivalent to EnumWindows + GetClassName).
    /// </summary>
    internal static List<TaskbarInfo> EnumerateSecondaryTaskbars()
    {
        List<TaskbarInfo> taskbars = [];
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = NativeMethods.FindWindowExW(IntPtr.Zero, secondary, SecondaryTaskbarClass, null)) != IntPtr.Zero)
        {
            taskbars.Add(BuildInfo(secondary, isPrimary: false));
        }

        return taskbars;
    }

    /// <summary>Finds the SECONDARY taskbar currently hosted on <paramref name="monitor"/>, or null
    /// when that monitor has no secondary taskbar (display removed, or taskbar mid-recreation). Never
    /// returns the primary Shell_TrayWnd: a secondary widget must never resolve onto the primary bar
    /// even if their monitor handles momentarily coincide during a display transition.</summary>
    internal static TaskbarInfo? TryGetSecondaryTaskbarForMonitor(IntPtr monitor)
    {
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = NativeMethods.FindWindowExW(IntPtr.Zero, secondary, SecondaryTaskbarClass, null)) != IntPtr.Zero)
        {
            if (NativeMethods.MonitorFromWindow(secondary, NativeMethods.MONITOR_DEFAULTTONEAREST) == monitor)
            {
                return BuildInfo(secondary, isPrimary: false);
            }
        }

        return null;
    }

    private static TaskbarInfo BuildInfo(IntPtr taskbar, bool isPrimary)
    {
        // Secondary taskbars have no TrayNotifyWnd; the descendant walk simply yields Zero there and
        // callers fall back to the taskbar's far edge for anchoring.
        IntPtr tray = FindDescendant(taskbar, "TrayNotifyWnd");
        _ = NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT taskbarRect);
        _ = NativeMethods.GetClientRect(taskbar, out NativeMethods.RECT clientRect);
        NativeMethods.RECT trayRect = default;
        if (tray != IntPtr.Zero)
        {
            _ = NativeMethods.GetWindowRect(tray, out trayRect);
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTONEAREST);
        long exStyle = NativeMethods.GetWindowLongPtrW(taskbar, NativeMethods.GWL_EXSTYLE).ToInt64();
        int edge = isPrimary ? GetTaskbarEdge() : GuessEdgeFromMonitor(taskbarRect, monitor);

        return new TaskbarInfo(
            taskbar,
            tray,
            monitor,
            isPrimary,
            ToRectangle(taskbarRect),
            ToRectangle(clientRect),
            ToRectangle(trayRect),
            edge,
            (exStyle & NativeMethods.WS_EX_LAYOUTRTL) != 0,
            IsExplorerProcess(taskbar));
    }

    /// <summary>
    /// Infers which monitor edge a secondary taskbar hugs. ABM_GETTASKBARPOS only reports the primary
    /// taskbar, so for secondaries the edge is derived geometrically: a wide bar is top/bottom, a tall
    /// bar left/right, disambiguated by which half of the monitor its centre sits in.
    /// </summary>
    private static int GuessEdgeFromMonitor(NativeMethods.RECT taskbarRect, IntPtr monitor)
    {
        NativeMethods.MONITORINFO info = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (taskbarRect.IsEmpty || monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return NativeMethods.ABE_BOTTOM;
        }

        if (taskbarRect.Width >= taskbarRect.Height)
        {
            int barCenterY = taskbarRect.Top + (taskbarRect.Height / 2);
            int monitorCenterY = info.rcMonitor.Top + (info.rcMonitor.Height / 2);
            return barCenterY >= monitorCenterY ? NativeMethods.ABE_BOTTOM : NativeMethods.ABE_TOP;
        }

        int barCenterX = taskbarRect.Left + (taskbarRect.Width / 2);
        int monitorCenterX = info.rcMonitor.Left + (info.rcMonitor.Width / 2);
        return barCenterX >= monitorCenterX ? NativeMethods.ABE_RIGHT : NativeMethods.ABE_LEFT;
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

    /// <summary>
    /// Screen rect of the centered app band (Start/pinned/running task buttons), used to keep the widget
    /// from overlapping it. Returns null on builds where the classic MSTask*/rebar windows aren't present
    /// (the caller then falls back to the taskbar centre).
    /// </summary>
    internal static Rectangle? TryGetAppClusterRect(IntPtr taskbar)
    {
        foreach (var className in new[] { "MSTaskListWClass", "MSTaskSwWClass", "ReBarWindow32" })
        {
            IntPtr hwnd = FindDescendant(taskbar, className);
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect) && rect.Right > rect.Left)
            {
                return ToRectangle(rect);
            }
        }

        return null;
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
