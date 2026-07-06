using System.Drawing;
using System.Threading;

namespace CodexWinBar.Widget;

/// <summary>
/// Hosts one CodexWinBar taskbar widget per taskbar — the primary Shell_TrayWnd plus every secondary
/// Shell_SecondaryTrayWnd (Windows' "show taskbar on all displays") — each on its own dedicated STA
/// thread running its own <see cref="WidgetWindow"/>. Instances are keyed by the taskbar's monitor
/// handle and reconciled on a periodic timer, so widgets appear/disappear as displays (and their
/// taskbars) come and go.
/// </summary>
public sealed class WidgetHost : IWidgetHost
{
    /// <summary>
    /// Cadence of the taskbar-set reconcile. A periodic timer was chosen over a hidden
    /// WM_DISPLAYCHANGE message window because the host itself owns no message loop (each widget
    /// instance already reacts to WM_DISPLAYCHANGE/TaskbarCreated for its OWN repositioning); the
    /// timer only handles instance add/remove, where a couple seconds of latency is fine. Matches the
    /// widget's own overlay poll cadence.
    /// </summary>
    private const int ReconcileIntervalMs = 2000;

    private readonly Lock _gate = new();

    // Serializes instance lifecycle (Start/Stop/reconcile). Lock order: _lifecycle before _gate.
    // Widget threads only ever take _gate, so joining a widget thread while holding _lifecycle
    // cannot deadlock.
    private readonly Lock _lifecycle = new();

    /// <summary>Live widget instances keyed by the taskbar's monitor handle. Guarded by _gate.</summary>
    private readonly Dictionary<IntPtr, WidgetInstance> _instances = [];

    private Timer? _reconcileTimer;
    private bool _started;
    private WidgetMode _requestedMode;
    private bool _anchorLeft;
    private WidgetRenderState _latestState = new() { Chips = [] };
    private WidgetMode _effectiveMode = WidgetMode.Hidden;
    private bool _disposed;

    /// <summary>Raised when any widget instance is left-clicked, with THAT instance's anchor rectangle
    /// (physical pixels on its own monitor) and optional provider key.</summary>
    public event Action<Rectangle, string?>? Clicked;

    /// <summary>Raised when any widget instance is right-clicked.</summary>
    public event Action? RightClicked;

    /// <summary>Raised when any instance's effective mode changes (the aggregate
    /// <see cref="EffectiveMode"/> tracks the primary taskbar's instance).</summary>
    public event Action<WidgetMode, string>? ModeChanged;

    /// <summary>Gets the mode currently in effect on the primary taskbar's widget after probing and fallback.</summary>
    public WidgetMode EffectiveMode
    {
        get
        {
            lock (_gate)
            {
                return _effectiveMode;
            }
        }
    }

    /// <summary>Sets the logger used by the widget implementation.</summary>
    public static void SetLogger(Action<string> logger)
    {
        WidgetLog.Sink = logger;
    }

    /// <inheritdoc />
    public Rectangle? CurrentScreenRect
    {
        get
        {
            lock (_gate)
            {
                Rectangle fallback = Rectangle.Empty;
                foreach (WidgetInstance instance in _instances.Values)
                {
                    Rectangle rect = instance.Window?.CurrentScreenRect ?? Rectangle.Empty;
                    if (rect.IsEmpty)
                    {
                        continue;
                    }

                    if (instance.IsPrimary)
                    {
                        return rect;
                    }

                    if (fallback.IsEmpty)
                    {
                        fallback = rect;
                    }
                }

                return fallback.IsEmpty ? null : fallback;
            }
        }
    }

    /// <summary>Starts one widget per taskbar and begins reconciling the set as displays change.</summary>
    public void Start(WidgetMode mode, bool anchorLeft = false)
    {
        ThrowIfDisposed();
        lock (_lifecycle)
        {
            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _requestedMode = mode;
                _anchorLeft = anchorLeft;
            }

            Reconcile();
            _reconcileTimer = new Timer(OnReconcileTimer, null, ReconcileIntervalMs, ReconcileIntervalMs);
        }
    }

    /// <summary>Posts a new immutable render state to every widget thread.</summary>
    public void Update(WidgetRenderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ThrowIfDisposed();
        List<WidgetWindow> windows = [];
        lock (_gate)
        {
            _latestState = state;
            foreach (WidgetInstance instance in _instances.Values)
            {
                if (instance.Window is { } window)
                {
                    window.SetState(state);
                    windows.Add(window);
                }
            }
        }

        foreach (WidgetWindow window in windows)
        {
            IntPtr controller = window.Controller;
            if (controller != IntPtr.Zero)
            {
                _ = NativeMethods.PostMessageW(controller, NativeMethods.WM_APP + 1, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    /// <summary>Stops every widget instance, waiting up to five seconds per thread.</summary>
    public void Stop()
    {
        lock (_lifecycle)
        {
            _reconcileTimer?.Dispose();
            _reconcileTimer = null;

            List<WidgetInstance> instances;
            lock (_gate)
            {
                _started = false;
                instances = [.. _instances.Values];
            }

            foreach (WidgetInstance instance in instances)
            {
                if (instance.Stop())
                {
                    lock (_gate)
                    {
                        _instances.Remove(instance.Monitor);
                    }
                }

                // else: keep the instance so a later Start cannot create a duplicate widget on its
                // monitor while the old STA thread may still own a live window; the next Stop retries.
            }

            lock (_gate)
            {
                if (_instances.Count == 0)
                {
                    _effectiveMode = WidgetMode.Hidden;
                }
            }
        }
    }

    /// <summary>Stops the widget host and releases resources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    internal void SetEffectiveModeFromThread(bool isPrimary, WidgetMode mode, string reason)
    {
        if (isPrimary)
        {
            lock (_gate)
            {
                _effectiveMode = mode;
            }
        }

        ModeChanged?.Invoke(mode, reason);
    }

    internal void RaiseClicked(Rectangle rect, string? providerKey)
    {
        Clicked?.Invoke(rect, providerKey);
    }

    internal void RaiseRightClicked()
    {
        RightClicked?.Invoke();
    }

    private void OnReconcileTimer(object? state)
    {
        // Skip the tick when a lifecycle operation (Start/Stop/another reconcile) is in flight;
        // reconciliation is idempotent and simply runs again on the next tick.
        if (!_lifecycle.TryEnter())
        {
            return;
        }

        try
        {
            bool started;
            lock (_gate)
            {
                started = _started;
            }

            if (started)
            {
                Reconcile();
            }
        }
        catch (Exception ex)
        {
            WidgetLog.Write("Taskbar reconcile failed: " + ex);
        }
        finally
        {
            _lifecycle.Exit();
        }
    }

    /// <summary>
    /// Aligns the instance set with the taskbars currently present: starts an instance for every
    /// taskbar whose monitor has none, and stops instances whose taskbar vanished (display removed) or
    /// whose primary/secondary role changed (the replacement starts on a later tick, once the old
    /// thread is gone). Caller must hold _lifecycle.
    /// </summary>
    private void Reconcile()
    {
        List<TaskbarInfo> taskbars = TaskbarInterop.EnumerateTaskbars();

        WidgetMode mode;
        bool anchorLeft;
        List<WidgetInstance>? toStop = null;
        List<TaskbarInfo>? toStart = null;
        lock (_gate)
        {
            mode = _requestedMode;
            anchorLeft = _anchorLeft;
            foreach (WidgetInstance instance in _instances.Values)
            {
                bool stillPresent = false;
                for (int i = 0; i < taskbars.Count; i++)
                {
                    if (taskbars[i].Monitor == instance.Monitor && taskbars[i].IsPrimary == instance.IsPrimary)
                    {
                        stillPresent = true;
                        break;
                    }
                }

                if (!stillPresent)
                {
                    (toStop ??= []).Add(instance);
                }
            }

            for (int i = 0; i < taskbars.Count; i++)
            {
                if (!_instances.ContainsKey(taskbars[i].Monitor))
                {
                    (toStart ??= []).Add(taskbars[i]);
                }
            }
        }

        if (toStop is not null)
        {
            foreach (WidgetInstance instance in toStop)
            {
                WidgetLog.Write("Taskbar gone on monitor 0x" + instance.Monitor.ToString("X") + "; stopping its widget.");
                if (instance.Stop())
                {
                    lock (_gate)
                    {
                        _instances.Remove(instance.Monitor);
                        if (instance.IsPrimary)
                        {
                            _effectiveMode = WidgetMode.Hidden;
                        }
                    }
                }

                // else: the zombie stays keyed to its monitor so no duplicate can start there; the
                // stop is retried on the next reconcile tick.
            }
        }

        if (toStart is not null)
        {
            foreach (TaskbarInfo info in toStart)
            {
                WidgetLog.Write("Taskbar found on monitor 0x" + info.Monitor.ToString("X") + (info.IsPrimary ? " (primary)" : " (secondary)") + "; starting a widget.");
                WidgetInstance instance = new(this, info.Monitor, info.IsPrimary);
                lock (_gate)
                {
                    _instances[info.Monitor] = instance;
                }

                instance.Start(mode, anchorLeft);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// One widget: a dedicated STA thread running a <see cref="WidgetWindow"/> bound to the taskbar on
    /// one monitor. Start/Stop replicate the single-widget handshake (ready event, ControllerReady,
    /// QuitMessage, keep-state-on-failed-join) per instance. Lifecycle methods are serialized by the
    /// host's _lifecycle lock.
    /// </summary>
    private sealed class WidgetInstance
    {
        private readonly WidgetHost _host;
        private Thread? _thread;

        // Startup-ready handshake: set by the widget thread once its controller window exists (or, as
        // a backstop, when the thread exits), so Stop never signals quit before there is a window to
        // hear it.
        private ManualResetEventSlim? _ready;

        internal WidgetInstance(WidgetHost host, IntPtr monitor, bool isPrimary)
        {
            _host = host;
            Monitor = monitor;
            IsPrimary = isPrimary;
        }

        /// <summary>The monitor handle whose taskbar this instance targets (its identity key).</summary>
        internal IntPtr Monitor { get; }

        /// <summary>Whether this instance targets the primary taskbar (drives the aggregate mode/rect).</summary>
        internal bool IsPrimary { get; }

        /// <summary>The window, published by the widget thread under the host gate before Run.</summary>
        internal WidgetWindow? Window { get; private set; }

        internal void Start(WidgetMode mode, bool anchorLeft)
        {
            ManualResetEventSlim ready = new(false);
            _ready = ready;
            _thread = new Thread(() =>
            {
                try
                {
                    WidgetWindow window = new(_host, mode, anchorLeft, Monitor, IsPrimary)
                    {
                        // Signaled on the widget thread once the controller window is created (top of
                        // the message loop), so Stop can safely post the quit request.
                        ControllerReady = ready.Set,
                    };
                    lock (_host._gate)
                    {
                        Window = window;
                        window.SetState(_host._latestState);
                    }

                    window.Run();
                }
                finally
                {
                    // Backstop: if the thread dies before creating the controller, don't leave Stop
                    // waiting out its readiness timeout.
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "CodexWinBar.Widget[0x" + Monitor.ToString("X") + "]",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>Stops the instance thread, waiting up to five seconds; returns false when the
        /// thread would not exit (the instance must then be kept so no duplicate is started).</summary>
        internal bool Stop()
        {
            Thread? thread = _thread;
            if (thread is null)
            {
                return true;
            }

            // Wait briefly for the startup handshake so a Stop racing a fresh Start doesn't post the
            // quit request before the controller window exists (which would orphan the STA thread and
            // allow a later reconcile to create a second widget on this monitor).
            ManualResetEventSlim? ready = _ready;
            try
            {
                if (ready is not null && !ready.Wait(TimeSpan.FromSeconds(5)))
                {
                    WidgetLog.Write("Widget thread for monitor 0x" + Monitor.ToString("X") + " did not become ready within 5 seconds.");
                }
            }
            catch (ObjectDisposedException)
            {
                // A previous Stop finished and disposed the event; the thread is already down (or
                // going down) — fall through to the join below.
            }

            // Re-read the window under the gate: it is published by the widget thread before Run, so
            // after a successful handshake it is guaranteed to be visible here.
            WidgetWindow? window;
            lock (_host._gate)
            {
                window = Window;
            }

            IntPtr controller = window?.Controller ?? IntPtr.Zero;
            if (controller != IntPtr.Zero)
            {
                _ = NativeMethods.PostMessageW(controller, WidgetWindow.QuitMessage, IntPtr.Zero, IntPtr.Zero);
            }

            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                // Leave _thread/Window intact: the STA thread may still own a live widget window, and
                // clearing state here would let a later reconcile create a duplicate. A subsequent
                // Stop retries the quit request and join.
                WidgetLog.Write("Widget thread for monitor 0x" + Monitor.ToString("X") + " did not stop within 5 seconds; keeping instance state so no duplicate widget is created.");
                return false;
            }

            _thread = null;
            lock (_host._gate)
            {
                Window = null;
            }

            // The thread has exited (its backstop already set the event), so disposal cannot race a Set.
            ready?.Dispose();
            _ready = null;
            return true;
        }
    }
}
