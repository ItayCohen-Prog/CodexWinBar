using System.Drawing;
using System.Threading;

namespace CodexWinBar.Widget;

internal static class WidgetLog
{
    internal static Action<string>? Sink { get; set; }

    internal static void Write(string message)
    {
        try
        {
            Sink?.Invoke("[CodexWinBar.Widget] " + message);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class WidgetWindow : IDisposable
{
    private const string WidgetClassName = "CodexWinBarWidget";
    private const string ControllerClassName = "CodexWinBarWidgetController";
    private const uint RenderMessage = NativeMethods.WM_APP + 1;
    private const uint RepositionMessage = NativeMethods.WM_APP + 2;
    private const uint RecreateMessage = NativeMethods.WM_APP + 3;
    private const nuint RepositionTimer = 10;
    private const nuint OverlayPollTimer = 11;
    private const nuint EmbedProbeRetryTimer = 12;

    private readonly WidgetHost _host;
    private readonly ThemeReader _theme = new();
    private readonly WidgetRenderer _renderer;
    private readonly NativeMethods.WndProc _controllerProc;
    private readonly NativeMethods.WndProc _widgetProc;
    private readonly NativeMethods.WinEventDelegate _eventProc;
    private readonly IntPtr _module;
    private readonly bool _anchorLeft;
    private WidgetMode _requestedMode;
    private WidgetMode _effectiveMode = WidgetMode.Hidden;
    private WidgetMode? _attemptedMode;
    private WidgetRenderState _state = new() { Chips = [] };
    private IntPtr _controller;
    private IntPtr _widget;
    private IntPtr _taskbar;
    private IntPtr _tray;
    private IntPtr _hook;
    private IntPtr _foregroundHook;
    private IntPtr _arrowCursor;
    private uint _taskbarCreatedMessage;
    private uint _dpi = 96;
    private int _width = 1;
    private int _height = 1;
    private bool _vertical;
    private int _hoveredIndex = -1;
    private bool _trackingMouse;
    private bool _overlayHidden;
    private int _probeFailures;
    private bool _overlayFallbackForSession;
    private DateTime _displayChangingUntilUtc;
    private Rectangle _placementRect;
    private Rectangle _screenRect;
    private readonly List<Rectangle> _chipBounds = [];

    internal WidgetWindow(WidgetHost host, WidgetMode requestedMode, bool anchorLeft)
    {
        _host = host;
        _requestedMode = requestedMode;
        _anchorLeft = anchorLeft;
        _renderer = new WidgetRenderer(_theme);
        _controllerProc = ControllerWndProc;
        _widgetProc = WidgetWndProc;
        _eventProc = WinEventProc;
        _module = NativeMethods.GetModuleHandleW(null);
    }

    internal IntPtr Controller => _controller;

    internal WidgetMode EffectiveMode => _effectiveMode;

    /// <summary>Last placed widget rect in physical screen pixels; Empty before first placement.
    /// Read cross-thread for flyout anchoring — a torn read is benign there.</summary>
    internal Rectangle CurrentScreenRect => _screenRect;

    internal void Run()
    {
        try
        {
            RegisterClasses();
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
            _controller = NativeMethods.CreateWindowExW(0, ControllerClassName, null, NativeMethods.WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
            InstallHooks();
            Recreate();
            Pump();
        }
        catch (Exception ex)
        {
            WidgetLog.Write("Widget thread failed: " + ex);
        }
        finally
        {
            Dispose();
        }
    }

    internal void SetState(WidgetRenderState state)
    {
        _state = state;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        if (_foregroundHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_widget != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyWindow(_widget);
            _widget = IntPtr.Zero;
        }

        if (_controller != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyWindow(_controller);
            _controller = IntPtr.Zero;
        }

        // Unregister the window classes (safe now that both windows are destroyed) so the NEXT Start
        // re-registers them with fresh WndProc delegates. If the class keeps pointing at this instance's
        // delegates, they get garbage-collected on teardown and a later message into the stale delegate
        // hard-crashes the whole process via Environment.FailFast — this is what killed the app when the
        // widget mode was changed (e.g. to Hidden), because a restart's RegisterClassExW silently no-ops
        // on the still-registered class and the window keeps calling the dead delegate.
        _ = NativeMethods.UnregisterClassW(WidgetClassName, _module);
        _ = NativeMethods.UnregisterClassW(ControllerClassName, _module);

        _renderer.Dispose();
    }

    private void Pump()
    {
        while (NativeMethods.GetMessageW(out NativeMethods.MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            try
            {
                _ = NativeMethods.TranslateMessage(message);
                _ = NativeMethods.DispatchMessageW(message);
            }
            catch (Exception ex)
            {
                WidgetLog.Write("Message dispatch failed: " + ex);
            }
        }
    }

    private void RegisterClasses()
    {
        // Without a class cursor the widget inherits Explorer's tray cursor state, which shows the
        // app-starting/busy cursor over our window; give the class the standard arrow.
        _arrowCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(NativeMethods.IDC_ARROW));

        NativeMethods.WNDCLASSEXW controller = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = _controllerProc,
            hInstance = _module,
            hCursor = _arrowCursor,
            lpszClassName = ControllerClassName,
        };
        _ = NativeMethods.RegisterClassExW(ref controller);

        NativeMethods.WNDCLASSEXW widget = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = _widgetProc,
            hInstance = _module,
            hCursor = _arrowCursor,
            lpszClassName = WidgetClassName,
        };
        _ = NativeMethods.RegisterClassExW(ref widget);
    }

    private void InstallHooks()
    {
        _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _eventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _eventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void Recreate()
    {
        _hoveredIndex = -1;
        TaskbarInfo? info = TaskbarInterop.TryGetPrimaryTaskbar();
        if (info is null)
        {
            SetEffectiveMode(WidgetMode.Hidden, "taskbar not found");
            return;
        }

        _taskbar = info.TaskbarHwnd;
        _tray = info.TrayHwnd;
        _dpi = NativeMethods.GetDpiForWindow(_taskbar);
        if (_dpi == 0)
        {
            _dpi = 96;
        }

        if (_requestedMode == WidgetMode.Hidden)
        {
            DestroyWidget();
            SetEffectiveMode(WidgetMode.Hidden, "hidden requested");
            return;
        }

        if (_anchorLeft && info.IsRtl)
        {
            CreateOverlayOrHidden("left-anchored rtl taskbar forces overlay");
            return;
        }

        // All four taskbar edges embed now (bottom/top render horizontally, left/right vertically).
        if (_requestedMode == WidgetMode.Overlay || _overlayFallbackForSession)
        {
            CreateOverlayOrHidden(_overlayFallbackForSession ? "embedded probe failed for session" : "overlay requested");
            return;
        }

        CreateEmbedded(info);
    }

    private static bool IsVerticalEdge(int edge) => edge == NativeMethods.ABE_LEFT || edge == NativeMethods.ABE_RIGHT;

    /// <summary>Sets orientation and computes _width/_height: the strip fills the taskbar's thickness on
    /// the cross-axis (client height when horizontal, client width when vertical) and the measured
    /// content length on the along-axis.</summary>
    private void MeasureForTaskbar(TaskbarInfo info)
    {
        _vertical = IsVerticalEdge(info.Edge);
        if (_vertical)
        {
            _width = Math.Max(1, info.ClientRect.Width);
            _height = _renderer.Measure(_state, _dpi, vertical: true, _chipBounds);
        }
        else
        {
            _height = Math.Max(1, info.ClientRect.Height);
            _width = _renderer.Measure(_state, _dpi, vertical: false, _chipBounds);
        }
    }

    private void CreateEmbedded(TaskbarInfo info)
    {
        DestroyWidget();
        _attemptedMode = WidgetMode.Embedded;
        MeasureForTaskbar(info);
        _widget = NativeMethods.CreateWindowExW(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED, WidgetClassName, null, NativeMethods.WS_POPUP, 0, 0, _width, _height, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
        IntPtr oldParent = NativeMethods.SetParent(_widget, info.TaskbarHwnd);

        long style = NativeMethods.GetWindowLongPtrW(_widget, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU);
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN;
        _ = NativeMethods.SetWindowLongPtrW(_widget, NativeMethods.GWL_STYLE, new IntPtr(style));
        long exStyle = NativeMethods.GetWindowLongPtrW(_widget, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        _ = NativeMethods.SetWindowLongPtrW(_widget, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        PositionEmbedded(info);
        _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOP, _placementRect.X, _placementRect.Y, _width, _height, NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        bool rendered = Render();
        bool valid = ProbeEmbedded(oldParent, rendered);
        if (valid)
        {
            _probeFailures = 0;
            _attemptedMode = null;
            SetEffectiveMode(WidgetMode.Embedded, "embedded");
            return;
        }

        CountProbeFailure("initial embedded probe failed");
    }

    private void CreateOverlayOrHidden(string reason)
    {
        DestroyWidget();
        TaskbarInfo? info = TaskbarInterop.TryGetPrimaryTaskbar();
        if (info is null || info.TrayHwnd == IntPtr.Zero)
        {
            SetEffectiveMode(WidgetMode.Hidden, reason + "; tray unavailable");
            return;
        }

        _taskbar = info.TaskbarHwnd;
        _tray = info.TrayHwnd;
        MeasureForTaskbar(info);
        _widget = NativeMethods.CreateWindowExW(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED, WidgetClassName, null, NativeMethods.WS_POPUP, 0, 0, _width, _height, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
        PositionOverlay(info);
        _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOPMOST, _screenRect.X, _screenRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _ = Render();
        SetEffectiveMode(WidgetMode.Overlay, reason);
        _ = NativeMethods.SetTimer(_controller, OverlayPollTimer, 2000, IntPtr.Zero);
    }

    private void DestroyWidget()
    {
        if (_controller != IntPtr.Zero)
        {
            _ = NativeMethods.KillTimer(_controller, EmbedProbeRetryTimer);
        }

        _attemptedMode = null;
        if (_widget != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyWindow(_widget);
            _widget = IntPtr.Zero;
        }
    }

    private void PositionEmbedded(TaskbarInfo info)
    {
        int gap = Scale(6);
        int x;
        int y;
        if (_vertical)
        {
            // Center across the strip width; anchor to the top of the taskbar (the "start" end) or,
            // when not left-anchored, just above the tray/clock at the bottom of a vertical taskbar.
            x = Math.Max(0, (info.ClientRect.Width - _width) / 2);
            if (_anchorLeft)
            {
                y = Scale(8);
            }
            else
            {
                NativeMethods.RECT trayClient = new() { Left = info.TrayRect.Left, Top = info.TrayRect.Top, Right = info.TrayRect.Right, Bottom = info.TrayRect.Bottom };
                _ = NativeMethods.MapWindowPoints(IntPtr.Zero, info.TaskbarHwnd, ref trayClient, 2);
                y = Math.Max(Scale(8), trayClient.Top - _height - gap);
            }
        }
        else
        {
            if (_anchorLeft)
            {
                x = Scale(8);
            }
            else
            {
                NativeMethods.RECT trayClient = new() { Left = info.TrayRect.Left, Top = info.TrayRect.Top, Right = info.TrayRect.Right, Bottom = info.TrayRect.Bottom };
                _ = NativeMethods.MapWindowPoints(IntPtr.Zero, info.TaskbarHwnd, ref trayClient, 2);
                x = trayClient.Left - _width - gap;
            }

            y = Math.Max(0, (info.ClientRect.Height - _height) / 2);
        }

        _placementRect = new Rectangle(x, y, _width, _height);
        NativeMethods.RECT screen = new() { Left = x, Top = y, Right = x + _width, Bottom = y + _height };
        _ = NativeMethods.MapWindowPoints(info.TaskbarHwnd, IntPtr.Zero, ref screen, 2);
        _screenRect = TaskbarInterop.ToRectangle(screen);
    }

    private void PositionOverlay(TaskbarInfo info)
    {
        int gap = Scale(6);
        int x;
        int y;
        if (_vertical)
        {
            x = info.TaskbarRect.Left + Math.Max(0, (info.TaskbarRect.Width - _width) / 2);
            y = _anchorLeft ? info.TaskbarRect.Top + Scale(8) : Math.Max(info.TaskbarRect.Top + Scale(8), info.TrayRect.Top - _height - gap);
        }
        else
        {
            x = _anchorLeft ? info.TaskbarRect.Left + Scale(8) : info.TrayRect.Left - _width - gap;
            y = info.TaskbarRect.Top + Math.Max(0, (info.TaskbarRect.Height - _height) / 2);
        }

        _placementRect = new Rectangle(x, y, _width, _height);
        _screenRect = _placementRect;
    }

    private bool Render()
    {
        if (_widget == IntPtr.Zero)
        {
            return false;
        }

        long style = NativeMethods.GetWindowLongPtrW(_widget, NativeMethods.GWL_STYLE).ToInt64();
        bool embeddedChild = (style & NativeMethods.WS_CHILD) != 0;
        Point? location = embeddedChild ? null : _screenRect.Location;
        return _renderer.RenderToWindow(_widget, _state, _width, _height, _dpi, _vertical, _hoveredIndex, location);
    }

    private bool ProbeEmbedded(IntPtr oldParent, bool rendered)
    {
        if (Suspended())
        {
            return true;
        }

        if (oldParent == IntPtr.Zero || _widget == IntPtr.Zero || _taskbar == IntPtr.Zero || !rendered)
        {
            return false;
        }

        bool ancestorMatches = false;
        IntPtr current = _widget;
        for (int i = 0; i < 8 && current != IntPtr.Zero; i++)
        {
            current = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
            if (current == _taskbar)
            {
                ancestorMatches = true;
                break;
            }
        }

        long style = NativeMethods.GetWindowLongPtrW(_widget, NativeMethods.GWL_STYLE).ToInt64();
        bool styleOk = (style & NativeMethods.WS_CHILD) != 0 && (style & NativeMethods.WS_POPUP) == 0;
        bool rectOk = NativeMethods.GetWindowRect(_widget, out NativeMethods.RECT rect) && !rect.IsEmpty;
        Rectangle widgetScreen = TaskbarInterop.ToRectangle(rect);
        bool intersects = TaskbarInterop.RectIntersectsTaskbarClient(_taskbar, widgetScreen);
        bool anchorOk = _anchorLeft ? LeftEdgeWithinTaskbarClient(widgetScreen) : _tray == IntPtr.Zero || widgetScreen.Right <= TaskbarInterop.ToRectangle(GetWindowRectOrEmpty(_tray)).Left + Scale(2);
        return ancestorMatches && TaskbarInterop.IsExplorerProcess(_taskbar) && styleOk && rectOk && intersects && anchorOk;
    }

    private bool LeftEdgeWithinTaskbarClient(Rectangle widgetScreen)
    {
        if (_taskbar == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.RECT client = new() { Left = 0, Top = 0, Right = 0, Bottom = 0 };
        if (!NativeMethods.GetClientRect(_taskbar, out client))
        {
            return false;
        }

        _ = NativeMethods.MapWindowPoints(_taskbar, IntPtr.Zero, ref client, 2);
        Rectangle taskbarClient = TaskbarInterop.ToRectangle(client);
        return widgetScreen.Left >= taskbarClient.Left && widgetScreen.Left <= taskbarClient.Right;
    }

    private static NativeMethods.RECT GetWindowRectOrEmpty(IntPtr hwnd)
    {
        return NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect) ? rect : default;
    }

    private void CountProbeFailure(string reason)
    {
        if (Suspended())
        {
            return;
        }

        _probeFailures++;
        WidgetLog.Write(reason + " (" + _probeFailures + ")");
        if (_probeFailures >= 2)
        {
            _overlayFallbackForSession = true;
            _attemptedMode = null;
            CreateOverlayOrHidden(reason);
            return;
        }

        if (_controller != IntPtr.Zero && _attemptedMode == WidgetMode.Embedded && _widget != IntPtr.Zero)
        {
            _ = NativeMethods.SetTimer(_controller, EmbedProbeRetryTimer, 500, IntPtr.Zero);
        }
    }

    private bool Suspended()
    {
        return TaskbarInterop.IsAutoHidden() || DateTime.UtcNow < _displayChangingUntilUtc || (_effectiveMode == WidgetMode.Overlay && _overlayHidden);
    }

    private void Reposition()
    {
        TaskbarInfo? info = TaskbarInterop.TryGetPrimaryTaskbar();
        if (info is null || _widget == IntPtr.Zero)
        {
            return;
        }

        _taskbar = info.TaskbarHwnd;
        _tray = info.TrayHwnd;
        _dpi = NativeMethods.GetDpiForWindow(_taskbar);
        if (_dpi == 0)
        {
            _dpi = 96;
        }

        MeasureForTaskbar(info);
        if (_effectiveMode == WidgetMode.Embedded || _attemptedMode == WidgetMode.Embedded)
        {
            PositionEmbedded(info);
            _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOP, _placementRect.X, _placementRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            bool rendered = Render();
            if (ProbeEmbedded(IntPtr.Zero + 1, rendered))
            {
                _probeFailures = 0;
                _attemptedMode = null;
                SetEffectiveMode(WidgetMode.Embedded, "embedded");
            }
            else
            {
                CountProbeFailure("embedded reprobe failed");
            }
        }
        else if (_effectiveMode == WidgetMode.Overlay)
        {
            PositionOverlay(info);
            UpdateOverlayVisibility();
            _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOPMOST, _placementRect.X, _placementRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | (_overlayHidden ? NativeMethods.SWP_HIDEWINDOW : NativeMethods.SWP_SHOWWINDOW));
            _ = Render();
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (_effectiveMode != WidgetMode.Overlay || _widget == IntPtr.Zero)
        {
            return;
        }

        bool hide = TaskbarInterop.IsAutoHidden() || FullscreenDetector.IsForegroundFullscreenOnMonitor(_taskbar);
        if (hide != _overlayHidden)
        {
            _overlayHidden = hide;
            _ = NativeMethods.ShowWindow(_widget, hide ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNOACTIVATE);
        }
    }

    private void SetEffectiveMode(WidgetMode mode, string reason)
    {
        if (_effectiveMode == mode)
        {
            return;
        }

        _effectiveMode = mode;
        if (mode != WidgetMode.Hidden)
        {
            _attemptedMode = null;
        }

        _host.SetEffectiveModeFromThread(mode, reason);
    }

    private IntPtr ControllerWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == _taskbarCreatedMessage)
            {
                _ = NativeMethods.PostMessageW(hwnd, RecreateMessage, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case RenderMessage:
                    Reposition();
                    return IntPtr.Zero;
                case RepositionMessage:
                    Reposition();
                    return IntPtr.Zero;
                case RecreateMessage:
                    Thread.Sleep(250);
                    for (int i = 0; i < 3 && TaskbarInterop.TryGetPrimaryTaskbar() is null; i++)
                    {
                        Thread.Sleep(1000);
                    }

                    Recreate();
                    return IntPtr.Zero;
                case NativeMethods.WM_DISPLAYCHANGE:
                case NativeMethods.WM_DPICHANGED:
                    _displayChangingUntilUtc = DateTime.UtcNow.AddSeconds(2);
                    _ = NativeMethods.PostMessageW(hwnd, RepositionMessage, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                case NativeMethods.WM_SETTINGCHANGE:
                    _theme.NotifySettingChanged();
                    _ = NativeMethods.PostMessageW(hwnd, RepositionMessage, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                case NativeMethods.WM_TIMER:
                    if ((nuint)wParam == RepositionTimer)
                    {
                        _ = NativeMethods.KillTimer(hwnd, RepositionTimer);
                        Reposition();
                    }
                    else if ((nuint)wParam == OverlayPollTimer)
                    {
                        UpdateOverlayVisibility();
                    }
                    else if ((nuint)wParam == EmbedProbeRetryTimer)
                    {
                        _ = NativeMethods.KillTimer(hwnd, EmbedProbeRetryTimer);
                        Reposition();
                    }

                    return IntPtr.Zero;
                case NativeMethods.WM_DESTROY:
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            WidgetLog.Write("Controller wndproc failed: " + ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr WidgetWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_SETCURSOR:
                    // Force the arrow over the whole widget; as a SetParent child of Explorer's tray
                    // the inherited cursor is otherwise the busy/app-starting spinner.
                    _ = NativeMethods.SetCursor(_arrowCursor);
                    return new IntPtr(1);
                case NativeMethods.WM_MOUSEMOVE:
                    TrackMouse(hwnd);
                    _hoveredIndex = HitTest(ClientPointFromLParam(lParam));
                    _ = Render();
                    return IntPtr.Zero;
                case NativeMethods.WM_MOUSELEAVE:
                    _trackingMouse = false;
                    _hoveredIndex = -1;
                    _ = Render();
                    return IntPtr.Zero;
                case NativeMethods.WM_LBUTTONUP:
                    WidgetLog.Write("widget clicked");
                    var clickedIndex = HitTest(ClientPointFromLParam(lParam));
                    if (clickedIndex >= 0 && clickedIndex < _state.Chips.Count && clickedIndex < _chipBounds.Count)
                    {
                        Rectangle bounds = ChipHitRect(clickedIndex);
                        var chipRect = new Rectangle(
                            _screenRect.Left + bounds.Left,
                            _screenRect.Top + bounds.Top,
                            bounds.Width,
                            bounds.Height);
                        _host.RaiseClicked(chipRect, _state.Chips[clickedIndex].ProviderKey);
                    }
                    else
                    {
                        _host.RaiseClicked(_screenRect, null);
                    }

                    return IntPtr.Zero;
                case NativeMethods.WM_RBUTTONUP:
                    _host.RaiseRightClicked();
                    return IntPtr.Zero;
                case NativeMethods.WM_NCHITTEST:
                    return new IntPtr(NativeMethods.HTCLIENT);
            }
        }
        catch (Exception ex)
        {
            WidgetLog.Write("Widget wndproc failed: " + ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void TrackMouse(IntPtr hwnd)
    {
        if (_trackingMouse)
        {
            return;
        }

        NativeMethods.TRACKMOUSEEVENT track = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
            dwFlags = NativeMethods.TME_LEAVE,
            hwndTrack = hwnd,
        };
        _trackingMouse = NativeMethods.TrackMouseEvent(ref track);
    }

    private int HitTest(Point clientPoint)
    {
        if (_state.Chips.Count == 0)
        {
            return -1;
        }

        if (_chipBounds.Count != _state.Chips.Count)
        {
            _ = _renderer.Measure(_state, _dpi, _vertical, _chipBounds);
        }

        for (int i = 0; i < _chipBounds.Count; i++)
        {
            if (ChipRegion(i).Contains(clientPoint))
            {
                return i;
            }
        }

        // Regions tile the whole widget [0,len); only an exact far-edge pixel misses — snap to the last.
        return _chipBounds.Count - 1;
    }

    /// <summary>The chip's tight client rect (icon + gauges), used to anchor the flyout at the icon.</summary>
    private Rectangle ChipHitRect(int i)
    {
        Rectangle bounds = _chipBounds[i];
        return _vertical ? bounds with { Width = _width } : bounds with { Height = _height };
    }

    /// <summary>
    /// The provider's full clickable region: the whole widget is tiled between providers, each region
    /// running from the midpoint of the gap with the previous chip to the midpoint with the next (the
    /// ends reach the widget edges). No dead "seam" between icons, and hover covers the whole area.
    /// </summary>
    private Rectangle ChipRegion(int i)
    {
        Rectangle self = _chipBounds[i];
        int last = _chipBounds.Count - 1;
        if (_vertical)
        {
            int top = i == 0 ? 0 : (_chipBounds[i - 1].Bottom + self.Top) / 2;
            int bottom = i == last ? _height : (self.Bottom + _chipBounds[i + 1].Top) / 2;
            return Rectangle.FromLTRB(0, top, _width, bottom);
        }

        int left = i == 0 ? 0 : (_chipBounds[i - 1].Right + self.Left) / 2;
        int right = i == last ? _width : (self.Right + _chipBounds[i + 1].Left) / 2;
        return Rectangle.FromLTRB(left, 0, right, _height);
    }

    private static Point ClientPointFromLParam(IntPtr lParam) => new((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_controller == IntPtr.Zero)
        {
            return;
        }

        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            _ = NativeMethods.PostMessageW(_controller, RepositionMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        if (hwnd == _taskbar || hwnd == _tray)
        {
            _ = NativeMethods.SetTimer(_controller, RepositionTimer, 250, IntPtr.Zero);
        }
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _dpi / 96.0));
}
