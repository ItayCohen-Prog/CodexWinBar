namespace CodexWinBar.Widget;

/// <summary>Full breakdown of the fullscreen check on a monitor, so diagnostics can see WHY it fired.</summary>
/// <remarks><see cref="IndeterminateForeground"/> flags a transient staging window holding foreground:
/// the check learned NOTHING this poll and the caller should keep its previous show/hide state.</remarks>
internal readonly record struct FullscreenInfo(
    bool IsFullscreen,
    IntPtr Foreground,
    bool SameMonitor,
    bool Zoomed,
    bool CoversMonitor,
    bool Borderless,
    uint WindowStyle,
    NativeMethods.RECT ForegroundRect,
    bool IndeterminateForeground = false);

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
            return new FullscreenInfo(false, foreground, false, false, false, false, 0, default);
        }

        // Clicking the wallpaper (or Win+D) makes the DESKTOP itself the foreground window — 'Progman',
        // or a 'WorkerW' wallpaper worker. That window spans the whole VIRTUAL screen, so the
        // covers-monitor test below reads it as a fullscreen app and hides the widget on every desktop
        // click (confirmed from a user's diagnostics: fg='Progman' fgRect=0,0,4480,1440 → HIDE). The
        // desktop is never a fullscreen app; taskbar utilities conventionally exclude these classes.
        string foregroundClass = NativeMethods.GetWindowClass(foreground);
        if (foregroundClass is "Progman" or "WorkerW")
        {
            return new FullscreenInfo(false, foreground, false, false, false, false, 0, default);
        }

        // Windows briefly focuses invisible staging windows during foreground handoffs (alt-tab, app
        // launches): 'ForegroundStaging' and the shell's 'XamlExplorerHostIslandWindow'. They say
        // nothing about what is actually on screen — reading them as "not fullscreen" popped the
        // widget over fullscreen video for the ~1s they held foreground (seen in user diagnostics).
        if (foregroundClass is "ForegroundStaging" or "XamlExplorerHostIslandWindow")
        {
            return new FullscreenInfo(false, foreground, false, false, false, false, 0, default, IndeterminateForeground: true);
        }

        IntPtr foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        bool sameMonitor = foregroundMonitor == monitor;
        bool zoomed = NativeMethods.IsZoomed(foreground);
        uint windowStyle = unchecked((uint)NativeMethods.GetWindowLongPtrW(foreground, NativeMethods.GWL_STYLE).ToInt64());
        bool borderless = IsBorderless(windowStyle);

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

        // IsZoomed alone cannot separate an ordinary maximized app from borderless fullscreen: Chrome
        // video and games can deliberately combine WS_MAXIMIZE with a decoration-free window covering
        // the monitor. Keep decorated maximized windows visible beside the taskbar, but suppress the
        // widget for a monitor-covering window when it is either not maximized or genuinely borderless.
        bool isFullscreen = IsFullscreenLayout(sameMonitor, zoomed, coversMonitor, windowStyle);
        return new FullscreenInfo(isFullscreen, foreground, sameMonitor, zoomed, coversMonitor, borderless, windowStyle, rect);
    }

    internal static bool IsFullscreenLayout(bool sameMonitor, bool zoomed, bool coversMonitor, uint windowStyle) =>
        sameMonitor && coversMonitor && (!zoomed || IsBorderless(windowStyle));

    internal static bool IsBorderless(uint windowStyle) =>
        windowStyle != 0 && (windowStyle & (NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME)) == 0;
}
