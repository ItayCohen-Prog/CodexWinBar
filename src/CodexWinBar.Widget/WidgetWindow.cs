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

    private readonly WidgetHost _host;
    private readonly ThemeReader _theme = new();
    private readonly WidgetRenderer _renderer;
    private readonly NativeMethods.WndProc _controllerProc;
    private readonly NativeMethods.WndProc _widgetProc;
    private readonly NativeMethods.WinEventDelegate _eventProc;
    private readonly IntPtr _module;
    private WidgetMode _requestedMode;
    private WidgetMode _effectiveMode = WidgetMode.Hidden;
    private WidgetRenderState _state = new() { Chips = [] };
    private IntPtr _controller;
    private IntPtr _widget;
    private IntPtr _taskbar;
    private IntPtr _tray;
    private IntPtr _hook;
    private IntPtr _foregroundHook;
    private uint _taskbarCreatedMessage;
    private uint _dpi = 96;
    private int _width = 1;
    private int _height = 1;
    private int _hoveredIndex = -1;
    private bool _trackingMouse;
    private bool _overlayHidden;
    private int _probeFailures;
    private bool _overlayFallbackForSession;
    private DateTime _displayChangingUntilUtc;
    private Rectangle _placementRect;
    private Rectangle _screenRect;

    internal WidgetWindow(WidgetHost host, WidgetMode requestedMode)
    {
        _host = host;
        _requestedMode = requestedMode;
        _renderer = new WidgetRenderer(_theme);
        _controllerProc = ControllerWndProc;
        _widgetProc = WidgetWndProc;
        _eventProc = WinEventProc;
        _module = NativeMethods.GetModuleHandleW(null);
    }

    internal IntPtr Controller => _controller;

    internal WidgetMode EffectiveMode => _effectiveMode;

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
        NativeMethods.WNDCLASSEXW controller = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = _controllerProc,
            hInstance = _module,
            lpszClassName = ControllerClassName,
        };
        _ = NativeMethods.RegisterClassExW(ref controller);

        NativeMethods.WNDCLASSEXW widget = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = _widgetProc,
            hInstance = _module,
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

        if (info.IsRtl)
        {
            CreateOverlayOrHidden("rtl taskbar forces overlay");
            return;
        }

        if (info.Edge != NativeMethods.ABE_BOTTOM)
        {
            CreateOverlayOrHidden("non-bottom taskbar forces overlay");
            return;
        }

        if (_requestedMode == WidgetMode.Overlay || _overlayFallbackForSession)
        {
            CreateOverlayOrHidden(_overlayFallbackForSession ? "embedded probe failed for session" : "overlay requested");
            return;
        }

        CreateEmbedded(info);
    }

    private void CreateEmbedded(TaskbarInfo info)
    {
        DestroyWidget();
        _height = Math.Max(1, info.ClientRect.Height);
        _width = _renderer.Measure(_state, _dpi);
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
        _height = Math.Max(1, info.ClientRect.Height);
        _width = _renderer.Measure(_state, _dpi);
        _widget = NativeMethods.CreateWindowExW(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED, WidgetClassName, null, NativeMethods.WS_POPUP, 0, 0, _width, _height, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
        PositionOverlay(info);
        _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOPMOST, _screenRect.X, _screenRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _ = Render();
        SetEffectiveMode(WidgetMode.Overlay, reason);
        _ = NativeMethods.SetTimer(_controller, OverlayPollTimer, 2000, IntPtr.Zero);
    }

    private void DestroyWidget()
    {
        if (_widget != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyWindow(_widget);
            _widget = IntPtr.Zero;
        }
    }

    private void PositionEmbedded(TaskbarInfo info)
    {
        NativeMethods.RECT trayClient = new() { Left = info.TrayRect.Left, Top = info.TrayRect.Top, Right = info.TrayRect.Right, Bottom = info.TrayRect.Bottom };
        _ = NativeMethods.MapWindowPoints(IntPtr.Zero, info.TaskbarHwnd, ref trayClient, 2);
        int gap = Scale(6);
        int x = trayClient.Left - _width - gap;
        int y = Math.Max(0, (info.ClientRect.Height - _height) / 2);
        _placementRect = new Rectangle(x, y, _width, _height);
        NativeMethods.RECT screen = new() { Left = x, Top = y, Right = x + _width, Bottom = y + _height };
        _ = NativeMethods.MapWindowPoints(info.TaskbarHwnd, IntPtr.Zero, ref screen, 2);
        _screenRect = TaskbarInterop.ToRectangle(screen);
    }

    private void PositionOverlay(TaskbarInfo info)
    {
        int gap = Scale(6);
        int x = info.TrayRect.Left - _width - gap;
        int y = info.TaskbarRect.Top + Math.Max(0, (info.TaskbarRect.Height - _height) / 2);
        _placementRect = new Rectangle(x, y, _width, _height);
        _screenRect = _placementRect;
    }

    private bool Render()
    {
        if (_widget == IntPtr.Zero)
        {
            return false;
        }

        Point location = _effectiveMode == WidgetMode.Embedded ? Point.Empty : _screenRect.Location;
        return _renderer.RenderToWindow(_widget, _state, _width, _height, _dpi, _hoveredIndex, location);
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
        bool adjacent = _tray == IntPtr.Zero || widgetScreen.Right <= TaskbarInterop.ToRectangle(GetWindowRectOrEmpty(_tray)).Left + Scale(2);
        return ancestorMatches && TaskbarInterop.IsExplorerProcess(_taskbar) && styleOk && rectOk && intersects && adjacent;
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
            CreateOverlayOrHidden(reason);
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

        _height = Math.Max(1, info.ClientRect.Height);
        _width = _renderer.Measure(_state, _dpi);
        if (_effectiveMode == WidgetMode.Embedded)
        {
            PositionEmbedded(info);
            _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOP, _placementRect.X, _placementRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            bool rendered = Render();
            if (!ProbeEmbedded(IntPtr.Zero + 1, rendered))
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
                case NativeMethods.WM_MOUSEMOVE:
                    TrackMouse(hwnd);
                    _hoveredIndex = HitTest(NativeMethods.GetCursorPos(out NativeMethods.POINT point) ? new Point(point.X, point.Y) : Point.Empty);
                    _ = Render();
                    return IntPtr.Zero;
                case NativeMethods.WM_MOUSELEAVE:
                    _trackingMouse = false;
                    _hoveredIndex = -1;
                    _ = Render();
                    return IntPtr.Zero;
                case NativeMethods.WM_LBUTTONUP:
                    _host.RaiseClicked(_screenRect);
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

    private int HitTest(Point screenPoint)
    {
        if (_state.Chips.Count == 0)
        {
            return -1;
        }

        int localX = screenPoint.X - _screenRect.X;
        using Bitmap bitmap = new(1, 1);
        using Graphics graphics = Graphics.FromImage(bitmap);
        using Font font = new("Segoe UI", 12.5f * _dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        int x = 0;
        float scale = _dpi / 96f;
        for (int i = 0; i < _state.Chips.Count; i++)
        {
            int width = ApproximateChipWidth(graphics, font, _state.Chips[i], scale);
            if (localX >= x && localX <= x + width)
            {
                return i;
            }

            x += width + Scale(10);
        }

        return -1;
    }

    private static int ApproximateChipWidth(Graphics graphics, Font font, WidgetChipState chip, float scale)
    {
        int width = (int)Math.Round(16 * scale) + (int)Math.Round(12 * scale);
        if (chip.SessionPercent.HasValue || chip.WeeklyPercent.HasValue)
        {
            width += (int)Math.Round(22 * scale);
        }

        if (!string.IsNullOrWhiteSpace(chip.Text))
        {
            width += (int)Math.Ceiling(graphics.MeasureString(chip.Text, font).Width) + (int)Math.Round(6 * scale);
        }

        return width;
    }

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
