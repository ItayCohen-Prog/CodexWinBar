using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexWinBar.App.Interop;

/// <summary>Applies Windows 11 DWM chrome attributes to WPF windows.</summary>
public static class WpfDwm
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmsbtMainWindow = 2;

    /// <summary>
    /// Applies Mica main-window chrome to a normal WPF window. Requires the full recipe:
    /// extend the frame into the whole client area ("sheet of glass"), set the backdrop type,
    /// and make the WPF surface transparent so Mica composes behind the content —
    /// without the frame extension a framed WPF window renders a black client area.
    /// </summary>
    public static void ApplyWindowChrome(Window window, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyWindowChrome(window, dark);
            return;
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);
        ApplyChrome(window, dark, DwmsbtMainWindow);

        window.Background = Brushes.Transparent;
        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }
    }

    /// <summary>
    /// Keeps the native caption inside the nearest monitor's working area. WPF's CenterScreen
    /// placement can use the wrong coordinate scale on mixed-DPI monitors, leaving a responsive
    /// client area visible while the title bar is stranded above the monitor.
    /// </summary>
    public static void EnsureTitleBarVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo) ||
            !GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var origin = ClampWindowOrigin(windowRect, monitorInfo.WorkArea);
        if (origin.X == windowRect.Left && origin.Y == windowRect.Top)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            origin.X,
            origin.Y,
            0,
            0,
            SwpNoSize | SwpNoZorder | SwpNoActivate);
    }

    internal static PointInt ClampWindowOrigin(RectInt window, RectInt workArea)
    {
        const int minimumCaptionWidth = 160;
        var minimumX = workArea.Left - Math.Max(0, window.Width - minimumCaptionWidth);
        var maximumX = workArea.Right - minimumCaptionWidth;
        return new PointInt(
            Math.Clamp(window.Left, minimumX, maximumX),
            Math.Clamp(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 48)));
    }

    private static void ApplyChrome(Window window, bool dark, int backdrop)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyChrome(window, dark, backdrop);
            return;
        }

        var darkValue = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
        var corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointInt(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectInt(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;

        public readonly int Width => this.Right - this.Left;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt Monitor;
        public RectInt WorkArea;
        public uint Flags;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectInt rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
