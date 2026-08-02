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
    // Window class names are unique per instance (multiple widgets — one per taskbar — live in this
    // process, each on its own STA thread). Sharing a class name would silently bind every window to the
    // FIRST instance's WndProc delegates, routing another taskbar's messages into the wrong instance.
    private static int _instanceCounter;
    private readonly string _widgetClassName;
    private readonly string _controllerClassName;
    private const uint RenderMessage = NativeMethods.WM_APP + 1;
    private const uint RepositionMessage = NativeMethods.WM_APP + 2;
    private const uint RecreateMessage = NativeMethods.WM_APP + 3;

    /// <summary>Posted by <see cref="WidgetHost.Stop"/> to ask the widget thread to exit its message
    /// loop. A dedicated app message (not a raw WM_DESTROY) so quitting is an explicit request the
    /// controller answers with PostQuitMessage, independent of actual window destruction.</summary>
    internal const uint QuitMessage = NativeMethods.WM_APP + 4;
    private const nuint RepositionTimer = 10;
    private const nuint OverlayPollTimer = 11;
    private const nuint BobTimer = 13;
    private const nuint StartLayoutRefreshTimer = 14;
    private const nuint ZOrderReassertTimer = 15;
    private const uint BobIntervalMs = 10;
    private const int ZOrderReassertTicks = 5;

    private readonly WidgetHost _host;
    private readonly ThemeReader _theme = new();
    private readonly WidgetRenderer _renderer;
    private readonly NativeMethods.WndProc _controllerProc;
    private readonly NativeMethods.WndProc _widgetProc;
    private readonly NativeMethods.WinEventDelegate _eventProc;
    private readonly IntPtr _module;

    // The monitor (HMONITOR) whose taskbar this instance targets — its stable identity. The taskbar
    // hwnd is re-resolved from it (see ResolveTaskbar) so Explorer restarts, which recreate every
    // taskbar window, don't strand the instance.
    private readonly IntPtr _monitor;
    private readonly bool _isPrimary;
    private WidgetMode _requestedMode;
    private WidgetMode _effectiveMode = WidgetMode.Hidden;
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
    private readonly System.Diagnostics.Stopwatch _bobWatch = new();
    private bool _bobActive;
    private bool _bobPeriodRaised;
    private int _lastBobOffset;
    private bool _trackingMouse;
    private bool _overlayHidden;
    private int _zOrderReassertTicksLeft;
    private Rectangle _placementRect;
    private readonly Lock _screenRectGate = new();
    private Rectangle _screenRect;
    private IntPtr _startLayoutTaskbar;
    private uint _startLayoutDpi;
    private Rectangle _startLayoutTaskbarRect;
    private DateTime _startLayoutCheckedUtc;
    private int _startInset;
    private Rectangle? _appClusterRect;
    private bool _horizontalLayoutKnown;
    private bool _layoutHasRoom = true;
    private int? _loggedOccupiedRight;
    private Rectangle? _loggedAppClusterRect;
    private bool _startLayoutLogged;
    private bool _fitLogged;
    private int _loggedBudget = int.MinValue;
    private int _loggedFitWidth = int.MinValue;
    private bool _loggedAnchorStart;
    private bool _overlayDecisionLogged;
    private bool _lastLoggedOverlayHide;
    private bool _controllerClassRegistered;
    private bool _widgetClassRegistered;
    private bool _initializationSignaled;
    private readonly List<Rectangle> _chipBounds = [];

    internal WidgetWindow(WidgetHost host, WidgetMode requestedMode, IntPtr monitor, bool isPrimary)
    {
        _host = host;
        _requestedMode = requestedMode;
        _monitor = monitor;
        _isPrimary = isPrimary;
        int instanceId = Interlocked.Increment(ref _instanceCounter);
        _widgetClassName = "CodexWinBarWidget_" + instanceId;
        _controllerClassName = "CodexWinBarWidgetController_" + instanceId;
        _renderer = new WidgetRenderer(_theme);
        _controllerProc = ControllerWndProc;
        _widgetProc = WidgetWndProc;
        _eventProc = WinEventProc;
        _module = NativeMethods.GetModuleHandleW(null);
    }

    /// <summary>Re-resolves this instance's taskbar (survives taskbar hwnd recreation). The primary
    /// instance always targets the one Shell_TrayWnd directly, so a drifting monitor handle can never
    /// strand it or spawn a duplicate; a secondary instance resolves only among Shell_SecondaryTrayWnd
    /// bars on its own monitor, so it can never latch onto the primary bar.</summary>
    private TaskbarInfo? ResolveTaskbar() => _isPrimary
        ? TaskbarInterop.TryGetPrimaryTaskbar()
        : TaskbarInterop.TryGetSecondaryTaskbarForMonitor(_monitor);

    internal IntPtr Controller => _controller;

    /// <summary>Invoked once on the widget thread after initialization succeeds or fails. Set by
    /// <see cref="WidgetHost.Start"/> before Run so Stop can safely distinguish startup from a live pump.</summary>
    internal Action<bool>? InitializationCompleted { get; set; }

    internal WidgetMode EffectiveMode => _effectiveMode;

    /// <summary>Last placed widget rect in physical screen pixels; Empty before first placement.</summary>
    internal Rectangle CurrentScreenRect => ReadScreenRect();

    private Rectangle ReadScreenRect()
    {
        lock (_screenRectGate)
        {
            return _screenRect;
        }
    }

    private void WriteScreenRect(Rectangle rect)
    {
        lock (_screenRectGate)
        {
            _screenRect = rect;
        }
    }

    internal void Run()
    {
        try
        {
            if (!RegisterClasses())
            {
                SignalInitialization(false);
                return;
            }

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
            _controller = NativeMethods.CreateWindowExW(0, _controllerClassName, null, NativeMethods.WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
            if (_controller == IntPtr.Zero)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                WidgetLog.Write($"CreateWindowExW failed for controller window (Win32 error {error}).");
                SignalInitialization(false);
                return;
            }

            InstallHooks();
            if (!Recreate())
            {
                SignalInitialization(false);
                return;
            }

            // Startup-ready handshake: the controller now exists, so a QuitMessage posted by Stop
            // will be delivered (posted messages queue even before the pump starts).
            SignalInitialization(true);
            Pump();
        }
        catch (Exception ex)
        {
            WidgetLog.Write("Widget thread failed: " + ex);
        }
        finally
        {
            SignalInitialization(false);
            Dispose();
        }
    }

    internal void SetState(WidgetRenderState state)
    {
        _state = state;
    }

    public void Dispose()
    {
        RestoreTimerResolution();
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

        // Unregister this instance's window classes (safe now that both windows are destroyed). The
        // classes point at this instance's WndProc delegates, which get garbage-collected after
        // teardown — a later message into a stale delegate hard-crashes the whole process via
        // Environment.FailFast. Class names are unique per instance (see _instanceCounter), so this
        // never races another live widget's registration.
        if (_widgetClassRegistered)
        {
            _ = NativeMethods.UnregisterClassW(_widgetClassName, _module);
            _widgetClassRegistered = false;
        }

        if (_controllerClassRegistered)
        {
            _ = NativeMethods.UnregisterClassW(_controllerClassName, _module);
            _controllerClassRegistered = false;
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

    private bool RegisterClasses()
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
            lpszClassName = _controllerClassName,
        };
        if (!RegisterClass(ref controller, _controllerClassName, out _controllerClassRegistered))
        {
            return false;
        }

        NativeMethods.WNDCLASSEXW widget = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = _widgetProc,
            hInstance = _module,
            hCursor = _arrowCursor,
            lpszClassName = _widgetClassName,
        };
        return RegisterClass(ref widget, _widgetClassName, out _widgetClassRegistered);
    }

    private static bool RegisterClass(ref NativeMethods.WNDCLASSEXW windowClass, string className, out bool registered)
    {
        ushort atom = NativeMethods.RegisterClassExW(ref windowClass);
        registered = atom != 0;
        if (registered)
        {
            return true;
        }

        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (IsClassRegistrationAccepted(atom, error))
        {
            return true;
        }

        WidgetLog.Write($"RegisterClassExW failed for '{className}' (Win32 error {error}).");
        return false;
    }

    internal static bool IsClassRegistrationAccepted(ushort atom, int error) =>
        atom != 0 || error == NativeMethods.ERROR_CLASS_ALREADY_EXISTS;

    private void SignalInitialization(bool succeeded)
    {
        if (_initializationSignaled)
        {
            return;
        }

        _initializationSignaled = true;
        InitializationCompleted?.Invoke(succeeded);
    }

    private void InstallHooks()
    {
        _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _eventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _eventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private bool Recreate()
    {
        _hoveredIndex = -1;
        TaskbarInfo? info = ResolveTaskbar();
        if (info is null)
        {
            SetEffectiveMode(WidgetMode.Hidden, "taskbar not found");
            return true;
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
            return true;
        }

        // One rendering mode: a topmost overlay pinned to the taskbar. It is the only approach that
        // behaves identically on every Windows 11 build — embedding into the XAML taskbar was fragile and
        // fell back to this anyway — and it stays visible even when the taskbar auto-hides. "Hidden" above
        // (the user turning the widget off) is the only other state.
        return CreateOverlayOrHidden("overlay");
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
            _layoutHasRoom = true;
            _width = Math.Max(1, info.ClientRect.Width);
            _height = _renderer.Measure(_state, _dpi, vertical: true, int.MaxValue, _chipBounds);
        }
        else
        {
            RefreshHorizontalLayout(info);
            _height = Math.Max(1, info.ClientRect.Height);
            bool anchorStart = AnchorOppositeTray(info);
            int budget = HorizontalBudget(info, anchorStart);
            _width = _renderer.Measure(_state, _dpi, vertical: false, budget, _chipBounds);
            _layoutHasRoom = HasHorizontalRoom(budget, _width, _horizontalLayoutKnown, anchorStart);
            // The chosen tier (Full/Medium show text; Compact drops it) falls out of budget vs. the
            // measured strip width. Log the geometry when it changes so a machine that wrongly collapses
            // to Compact reveals exactly why (e.g. a negative/tiny budget or an over-measured cluster).
            if (!_fitLogged || _loggedBudget != budget || _loggedFitWidth != _width || _loggedAnchorStart != anchorStart)
            {
                WidgetLog.Write($"Widget horizontal fit: anchorStart={anchorStart}; budget={budget}px; stripWidth={_width}px; hasRoom={_layoutHasRoom}; startKnown={_horizontalLayoutKnown}; taskbar={info.TaskbarRect}; tray={info.TrayRect}; cluster={(_appClusterRect?.ToString() ?? "none")}");
                _fitLogged = true;
                _loggedBudget = budget;
                _loggedFitWidth = _width;
                _loggedAnchorStart = anchorStart;
            }
        }
    }

    /// <summary>Refreshes the measured start-side taskbar content without querying Explorer on every
    /// render/reposition. Setting and display changes invalidate this cache immediately.</summary>
    private void RefreshHorizontalLayout(TaskbarInfo info)
    {
        DateTime now = DateTime.UtcNow;
        bool cacheValid = SameHorizontalLayoutSource(
            _startLayoutTaskbar,
            _startLayoutDpi,
            _startLayoutTaskbarRect,
            info.TaskbarHwnd,
            _dpi,
            info.TaskbarRect) &&
            now - _startLayoutCheckedUtc < TimeSpan.FromSeconds(2);
        if (cacheValid)
        {
            return;
        }

        Rectangle? fallbackCluster = TaskbarInterop.TryGetAppClusterRect(info.TaskbarHwnd);
        bool sameSource = SameHorizontalLayoutSource(
            _startLayoutTaskbar,
            _startLayoutDpi,
            _startLayoutTaskbarRect,
            info.TaskbarHwnd,
            _dpi,
            info.TaskbarRect);
        TaskbarStartLayout layout = TaskbarStartOccupancy.Measure(info.TaskbarHwnd, info.TaskbarRect, fallbackCluster);
        _startLayoutTaskbar = info.TaskbarHwnd;
        _startLayoutDpi = _dpi;
        _startLayoutTaskbarRect = info.TaskbarRect;
        _startLayoutCheckedUtc = now;
        if (layout.Status == TaskbarStartLayoutStatus.Failed)
        {
            if (!sameSource)
            {
                _horizontalLayoutKnown = false;
                _appClusterRect = fallbackCluster;
                _startInset = Scale(8);
                _startLayoutLogged = false;
            }

            return;
        }

        _horizontalLayoutKnown = true;
        _appClusterRect = layout.AppClusterRect ?? fallbackCluster;
        _startInset = StartInset(info.TaskbarRect, layout.OccupiedRight, Scale(8));

        if (!_startLayoutLogged || _loggedOccupiedRight != layout.OccupiedRight || _loggedAppClusterRect != _appClusterRect)
        {
            string occupied = layout.OccupiedRight?.ToString() ?? "none";
            string cluster = _appClusterRect?.ToString() ?? "unknown";
            WidgetLog.Write($"Taskbar start layout for 0x{info.TaskbarHwnd.ToInt64():X}: occupiedRight={occupied}; inset={_startInset}; appCluster={cluster}");
            _loggedOccupiedRight = layout.OccupiedRight;
            _loggedAppClusterRect = _appClusterRect;
            _startLayoutLogged = true;
        }
    }

    private void InvalidateHorizontalLayout(bool discardPending = true)
    {
        _startLayoutCheckedUtc = DateTime.MinValue;
        if (discardPending)
        {
            TaskbarStartOccupancy.Invalidate(_taskbar);
        }
    }

    /// <summary>
    /// Free span (physical px) the horizontal strip may occupy without colliding with the centered
    /// taskbar apps: from the tray leftward (right-anchored) or from the left edge rightward
    /// (left-anchored) up to the app cluster. Falls back to the taskbar centre when the app band can't be
    /// located. A zero span is preserved so placement can hide instead of overlapping occupied UI.
    /// </summary>
    private int HorizontalBudget(TaskbarInfo info, bool anchorStart)
    {
        int gap = Scale(6);
        int center = info.TaskbarRect.Left + (info.TaskbarRect.Width / 2);
        Rectangle? cluster = _appClusterRect ?? TaskbarInterop.TryGetAppClusterRect(info.TaskbarHwnd);
        int budget;
        if (anchorStart)
        {
            int start = info.TaskbarRect.Left + Math.Max(Scale(8), _startInset);
            int clusterLeft = cluster?.Left ?? center;
            budget = clusterLeft - start - gap;
        }
        else
        {
            int clusterRight = cluster?.Right ?? center;
            // The tray's left edge bounds the strip only when the tray is actually to the RIGHT of the
            // app cluster (the usual LTR taskbar). On an RTL taskbar the tray sits on the LEFT, so
            // trayLeft is a small left-side X and (trayLeft - clusterRight) goes negative — clamped to 0
            // that forced the widget into its textless Compact tier. When the tray isn't to the right of
            // the cluster, bound by the taskbar's right edge instead.
            int trayLeft = info.TrayRect.IsEmpty ? info.TaskbarRect.Right : info.TrayRect.Left;
            int rightBound = trayLeft > clusterRight ? trayLeft : info.TaskbarRect.Right;
            budget = rightBound - clusterRight - (2 * gap);
        }

        return Math.Max(0, budget);
    }

    internal static int StartInset(Rectangle taskbarRect, int? occupiedRight, int padding) =>
        occupiedRight is null ? padding : Math.Max(padding, occupiedRight.Value - taskbarRect.Left + padding);

    internal static bool HasHorizontalRoom(int budget, int measuredWidth, bool startLayoutKnown, bool anchorStart) =>
        budget > 0 && measuredWidth <= budget && (!anchorStart || startLayoutKnown);

    internal static bool SameHorizontalLayoutSource(
        IntPtr cachedTaskbar,
        uint cachedDpi,
        Rectangle cachedTaskbarRect,
        IntPtr taskbar,
        uint dpi,
        Rectangle taskbarRect) =>
        cachedTaskbar == taskbar && cachedDpi == dpi && cachedTaskbarRect == taskbarRect;

    private bool CreateOverlayOrHidden(string reason)
    {
        DestroyWidget();
        TaskbarInfo? info = ResolveTaskbar();
        if (info is null)
        {
            SetEffectiveMode(WidgetMode.Hidden, reason + "; taskbar unavailable");
            return true;
        }

        // Secondary taskbars never have a tray; only treat a missing tray as fatal on the primary,
        // where its absence signals a broken/mid-recreation taskbar.
        if (info.IsPrimary && info.TrayHwnd == IntPtr.Zero)
        {
            SetEffectiveMode(WidgetMode.Hidden, reason + "; tray unavailable");
            return true;
        }

        _taskbar = info.TaskbarHwnd;
        _tray = info.TrayHwnd;
        MeasureForTaskbar(info);
        _widget = NativeMethods.CreateWindowExW(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED, _widgetClassName, null, NativeMethods.WS_POPUP, 0, 0, _width, _height, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
        if (_widget == IntPtr.Zero)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            WidgetLog.Write($"CreateWindowExW failed for overlay window (Win32 error {error}).");
            SetEffectiveMode(WidgetMode.Hidden, "overlay window creation failed");
            return false;
        }

        // OWN the overlay to its taskbar (GWLP_HWNDPARENT sets the owner, not the parent): the window
        // manager keeps owned windows above their owner ATOMICALLY, so when the shell raises the
        // taskbar (any click on the bar, activation changes) the widget rides above it in the same
        // operation instead of blinking out until a timer re-asserts topmost — that race was visible
        // as a flash on every flyout dismiss. Explorer restarts destroy owned windows with the bar;
        // the TaskbarCreated -> Recreate path already rebuilds the widget then. The timer-driven
        // re-asserts stay as a backstop for taskbar hwnd swaps that keep the widget alive.
        _ = NativeMethods.SetWindowLongPtrW(_widget, NativeMethods.GWLP_HWNDPARENT, _taskbar);
        PositionOverlay(info);
        Rectangle screenRect = ReadScreenRect();
        uint visibility = _layoutHasRoom ? NativeMethods.SWP_SHOWWINDOW : NativeMethods.SWP_HIDEWINDOW;
        _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOPMOST, screenRect.X, screenRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | visibility);
        _ = Render();
        UpdateBobTimer();
        SetEffectiveMode(WidgetMode.Overlay, reason);
        // Poll for fullscreen/auto-hide often enough that the overlay disappears promptly when a video
        // goes fullscreen (a topmost overlay would otherwise stay drawn over borderless-fullscreen apps).
        _ = NativeMethods.SetTimer(_controller, OverlayPollTimer, 400, IntPtr.Zero);
        _ = NativeMethods.SetTimer(_controller, StartLayoutRefreshTimer, 5000, IntPtr.Zero);
        return true;
    }

    private void DestroyWidget()
    {
        if (_controller != IntPtr.Zero)
        {
            _ = NativeMethods.KillTimer(_controller, BobTimer);
            _ = NativeMethods.KillTimer(_controller, StartLayoutRefreshTimer);
        }

        _bobActive = false;
        _bobWatch.Reset();
        RestoreTimerResolution();
        if (_widget != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyWindow(_widget);
            _widget = IntPtr.Zero;
        }
    }

    private void PositionOverlay(TaskbarInfo info)
    {
        int gap = Scale(6);
        // Always sit on the side opposite the tray/clock (auto-detected), never a user choice.
        bool anchorStart = AnchorOppositeTray(info);
        int x;
        int y;
        if (_vertical)
        {
            // No tray on secondary taskbars: anchor to the taskbar's far (bottom) edge instead.
            int trayTop = info.TrayRect.IsEmpty ? info.TaskbarRect.Bottom - Scale(2) : info.TrayRect.Top;
            x = info.TaskbarRect.Left + Math.Max(0, (info.TaskbarRect.Width - _width) / 2);
            y = anchorStart ? info.TaskbarRect.Top + Scale(8) : Math.Max(info.TaskbarRect.Top + Scale(8), trayTop - _height - gap);
        }
        else
        {
            // No tray on secondary taskbars: anchor to the taskbar's right edge instead.
            int trayLeft = info.TrayRect.IsEmpty ? info.TaskbarRect.Right - Scale(2) : info.TrayRect.Left;
            x = anchorStart ? info.TaskbarRect.Left + Math.Max(Scale(8), _startInset) : trayLeft - _width - gap;
            y = info.TaskbarRect.Top + Math.Max(0, (info.TaskbarRect.Height - _height) / 2);
        }

        _placementRect = new Rectangle(x, y, _width, _height);
        WriteScreenRect(_placementRect);
    }

    /// <summary>True when the widget should anchor to the taskbar's "start" side (left on a horizontal
    /// bar, top on a vertical one) because the tray/clock is on the opposite end. Secondary taskbars have
    /// no tray, so they default to the start side. This is auto-detected — there is no placement setting.</summary>
    private bool AnchorOppositeTray(TaskbarInfo info)
    {
        if (info.TrayRect.IsEmpty)
        {
            return true;
        }

        if (_vertical)
        {
            int center = info.TaskbarRect.Top + (info.TaskbarRect.Height / 2);
            int tray = info.TrayRect.Top + (info.TrayRect.Height / 2);
            return tray >= center;
        }

        int centerX = info.TaskbarRect.Left + (info.TaskbarRect.Width / 2);
        int trayX = info.TrayRect.Left + (info.TrayRect.Width / 2);
        return trayX >= centerX;
    }

    private bool Render()
    {
        if (!_layoutHasRoom)
        {
            return true;
        }

        if (_widget == IntPtr.Zero)
        {
            return false;
        }

        Point location = ReadScreenRect().Location;
        int bobMs = _bobActive ? (int)_bobWatch.ElapsedMilliseconds : 0;
        return _renderer.RenderToWindow(_widget, _state, _width, _height, _dpi, _vertical, _hoveredIndex, bobMs, location);
    }

    /// <summary>Starts or stops the bob animation timer based on whether any chip requests attention.</summary>
    private void UpdateBobTimer()
    {
        bool wanted = _widget != IntPtr.Zero && _controller != IntPtr.Zero && _layoutHasRoom;
        bool anyAttention = false;
        for (int i = 0; i < _state.Chips.Count; i++)
        {
            if (_state.Chips[i].Attention)
            {
                anyAttention = true;
                break;
            }
        }

        wanted = wanted && anyAttention;
        if (wanted == _bobActive)
        {
            return;
        }

        _bobActive = wanted;
        if (wanted)
        {
            _lastBobOffset = 0;
            _bobWatch.Restart();
            RaiseTimerResolution();
            _ = NativeMethods.SetTimer(_controller, BobTimer, BobIntervalMs, IntPtr.Zero);
        }
        else
        {
            _ = NativeMethods.KillTimer(_controller, BobTimer);
            _bobWatch.Reset();
            RestoreTimerResolution();
        }
    }

    private void RaiseTimerResolution()
    {
        if (!_bobPeriodRaised)
        {
            _ = NativeMethods.TimeBeginPeriod(1);
            _bobPeriodRaised = true;
        }
    }

    private void RestoreTimerResolution()
    {
        if (_bobPeriodRaised)
        {
            _ = NativeMethods.TimeEndPeriod(1);
            _bobPeriodRaised = false;
        }
    }

    private void Reposition()
    {
        TaskbarInfo? info = ResolveTaskbar();
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
        if (_effectiveMode == WidgetMode.Overlay)
        {
            PositionOverlay(info);
            UpdateOverlayVisibility();
            _ = NativeMethods.SetWindowPos(_widget, NativeMethods.HWND_TOPMOST, _placementRect.X, _placementRect.Y, _width, _height, NativeMethods.SWP_NOACTIVATE | (_overlayHidden ? NativeMethods.SWP_HIDEWINDOW : NativeMethods.SWP_SHOWWINDOW));
            _ = Render();
        }

        UpdateBobTimer();
    }

    // Full breakdown of the retract check, so diagnostics can see the measurements behind it.
    private readonly record struct RetractInfo(
        bool Retracted, bool AutoHideEnabled, int OnScreenThickness,
        NativeMethods.RECT BarRect, NativeMethods.RECT MonitorRect);

    // Describes whether an auto-hide taskbar is CURRENTLY retracted (slid off to its thin hover sliver),
    // as opposed to TaskbarInterop.IsAutoHidden(), which only reports that auto-hide is enabled. Windows
    // keeps the retracted bar full-size but positioned mostly off the monitor, so only a few pixels of its
    // thickness stay on-screen; that thin sliver is how we tell "gone" from "shown".
    private RetractInfo DescribeRetract(IntPtr monitor)
    {
        bool autoHide = TaskbarInterop.IsAutoHidden();
        if (!autoHide || _taskbar == IntPtr.Zero || monitor == IntPtr.Zero || !NativeMethods.GetWindowRect(_taskbar, out NativeMethods.RECT bar))
        {
            return new RetractInfo(false, autoHide, -1, default, default);
        }

        NativeMethods.MONITORINFO mi = new()
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref mi))
        {
            return new RetractInfo(false, autoHide, -1, bar, default);
        }

        int onScreenWidth = Math.Min(bar.Right, mi.rcMonitor.Right) - Math.Max(bar.Left, mi.rcMonitor.Left);
        int onScreenHeight = Math.Min(bar.Bottom, mi.rcMonitor.Bottom) - Math.Max(bar.Top, mi.rcMonitor.Top);
        // A bar's thickness is its short dimension (height for a bottom/top bar, width for a side bar).
        // When retracted, only a ~1-2px sliver stays on-screen; a shown bar is its full ~48px.
        int onScreenThickness = _vertical ? onScreenWidth : onScreenHeight;
        return new RetractInfo(onScreenThickness <= Scale(6), autoHide, onScreenThickness, bar, mi.rcMonitor);
    }

    /// <summary>
    /// Puts the overlay back on top of the topmost band without moving, resizing, or re-rendering it.
    /// The shell raises the taskbar above everything topmost whenever a window is activated; the widget
    /// must ride back above the bar or it shows through the taskbar's pixels as a blink-out.
    /// </summary>
    private void ReassertOverlayTopmost()
    {
        if (_effectiveMode == WidgetMode.Overlay && _widget != IntPtr.Zero && !_overlayHidden)
        {
            _ = NativeMethods.SetWindowPos(
                _widget,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (_effectiveMode != WidgetMode.Overlay || _widget == IntPtr.Zero)
        {
            return;
        }

        // Use the monitor the overlay is ACTUALLY on (not the cached _monitor, which can disagree with
        // the live layout on multi-monitor setups and then silently defeat the checks).
        IntPtr liveMonitor = NativeMethods.MonitorFromWindow(_widget, NativeMethods.MONITOR_DEFAULTTONEAREST);
        RetractInfo retract = DescribeRetract(liveMonitor);
        FullscreenInfo full = FullscreenDetector.Describe(liveMonitor, _widget);
        // A transient staging window holding foreground carries no information; acting on it flashed
        // the widget over fullscreen apps for the ~1s handoff. Keep the current state until the next
        // poll (400ms) sees a real foreground window.
        if (full.IndeterminateForeground)
        {
            return;
        }

        // The widget tracks the taskbar like a real part of it: it hides while an auto-hide bar is
        // retracted (slid away) and returns when it slides back up, and it hides for a genuine fullscreen
        // app. It does NOT hide merely because auto-hide is enabled — only while the bar is actually gone.
        bool hide = !_layoutHasRoom || retract.Retracted || full.IsFullscreen;
        LogOverlayDecision(hide, liveMonitor, retract, full);
        if (hide != _overlayHidden)
        {
            _overlayHidden = hide;
            _ = NativeMethods.ShowWindow(_widget, hide ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNOACTIVATE);
        }
    }

    // Comprehensive per-instance diagnostics for the overlay show/hide decision. Log the first healthy
    // decision and every change after it, so transitions remain visible without steady-state log noise.
    // Two widgets (one per taskbar) log independently; compare their lines to see why the monitors differ.
    private void LogOverlayDecision(bool hide, IntPtr liveMonitor, RetractInfo retract, FullscreenInfo full)
    {
        bool changed = ShouldLogOverlayDecision(_overlayDecisionLogged, _lastLoggedOverlayHide, hide);
        if (!changed)
        {
            return;
        }

        _overlayDecisionLogged = true;
        _lastLoggedOverlayHide = hide;

        string role = _isPrimary ? "primary" : "secondary";
        string fgClass = NativeMethods.GetWindowClass(full.Foreground);
        WidgetLog.Write(
            $"Overlay decision [{role} tb=0x{_taskbar.ToInt64():X} assignedMon=0x{_monitor.ToInt64():X} liveMon=0x{liveMonitor.ToInt64():X} dpi={_dpi} vertical={_vertical}]: " +
            $"HIDE={hide} changed={changed} | layoutHasRoom={_layoutHasRoom} | " +
            $"retract{{enabled={retract.AutoHideEnabled} thickness={retract.OnScreenThickness} retracted={retract.Retracted} bar={Fmt(retract.BarRect)} mon={Fmt(retract.MonitorRect)}}} | " +
            $"fullscreen{{fg=0x{full.Foreground.ToInt64():X} '{fgClass}' same={full.SameMonitor} zoomed={full.Zoomed} borderless={full.Borderless} style=0x{full.WindowStyle:X8} covers={full.CoversMonitor} isFull={full.IsFullscreen} fgRect={Fmt(full.ForegroundRect)}}} | " +
            $"placement={_placementRect.X},{_placementRect.Y},{_placementRect.Width}x{_placementRect.Height} overlayHidden={_overlayHidden}");
    }

    internal static bool ShouldLogOverlayDecision(bool alreadyLogged, bool previousHide, bool hide) =>
        !alreadyLogged || previousHide != hide;

    private static string Fmt(NativeMethods.RECT r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";

    private void SetEffectiveMode(WidgetMode mode, string reason)
    {
        if (_effectiveMode == mode)
        {
            return;
        }

        _effectiveMode = mode;
        _host.SetEffectiveModeFromThread(_isPrimary, mode, reason);
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
                    for (int i = 0; i < 3 && ResolveTaskbar() is null; i++)
                    {
                        Thread.Sleep(1000);
                    }

                    _ = Recreate();
                    return IntPtr.Zero;
                case NativeMethods.WM_DISPLAYCHANGE:
                case NativeMethods.WM_DPICHANGED:
                    InvalidateHorizontalLayout();
                    _ = NativeMethods.PostMessageW(hwnd, RepositionMessage, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                case NativeMethods.WM_SETTINGCHANGE:
                    _theme.NotifySettingChanged();
                    InvalidateHorizontalLayout();
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
                        // Standing backstop for taskbar raises with no foreground event (the burst
                        // below handles the common activation-driven raise within ~60ms).
                        ReassertOverlayTopmost();
                    }
                    else if ((nuint)wParam == ZOrderReassertTimer)
                    {
                        ReassertOverlayTopmost();
                        if (--_zOrderReassertTicksLeft <= 0)
                        {
                            _ = NativeMethods.KillTimer(hwnd, ZOrderReassertTimer);
                        }
                    }
                    else if ((nuint)wParam == BobTimer)
                    {
                        // Only redraw when the bob offset actually moved — during the ~1.1s rest phase
                        // between bounces the offset is a constant 0, and re-rendering ~100x/sec there
                        // is pure waste. The timer keeps running so the next hop resumes on schedule.
                        int bobMs = _bobActive ? (int)_bobWatch.ElapsedMilliseconds : 0;
                        int bobOffset = WidgetRenderer.BobOffset(bobMs, _dpi);
                        if (bobOffset != _lastBobOffset)
                        {
                            _lastBobOffset = bobOffset;
                            _ = Render();
                        }
                    }
                    else if ((nuint)wParam == StartLayoutRefreshTimer)
                    {
                        InvalidateHorizontalLayout(discardPending: false);
                        Reposition();
                    }

                    return IntPtr.Zero;
                case QuitMessage:
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
                    // Force the arrow over the whole overlay instead of inheriting transient shell state.
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
                    Rectangle screenRect = ReadScreenRect();
                    if (clickedIndex >= 0 && clickedIndex < _state.Chips.Count && clickedIndex < _chipBounds.Count)
                    {
                        Rectangle bounds = ChipHitRect(clickedIndex);
                        var chipRect = new Rectangle(
                            screenRect.Left + bounds.Left,
                            screenRect.Top + bounds.Top,
                            bounds.Width,
                            bounds.Height);
                        _host.RaiseClicked(chipRect, _state.Chips[clickedIndex].ProviderKey);
                    }
                    else
                    {
                        _host.RaiseClicked(screenRect, null);
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
            // Activating a window makes the shell RAISE THE TASKBAR within the topmost band —
            // often AFTER the reposition above has already re-asserted, so the taskbar lands over
            // the widget and it visibly blinks out until the next reposition (~250ms+, seen when
            // dismissing the flyout with a click). Beat the race with a short burst of cheap
            // z-order-only re-asserts. (OUTOFCONTEXT hooks run on this thread's message loop, so
            // touching the counter here is single-threaded.)
            _zOrderReassertTicksLeft = ZOrderReassertTicks;
            _ = NativeMethods.SetTimer(_controller, ZOrderReassertTimer, 60, IntPtr.Zero);
            return;
        }

        if (hwnd == _taskbar || hwnd == _tray)
        {
            InvalidateHorizontalLayout();
            _ = NativeMethods.SetTimer(_controller, RepositionTimer, 250, IntPtr.Zero);
        }
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _dpi / 96.0));
}
