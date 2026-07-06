using System.Drawing;
using System.Threading;

namespace CodexWinBar.Widget;

/// <summary>Hosts the CodexWinBar taskbar widget on a dedicated STA thread.</summary>
public sealed class WidgetHost : IWidgetHost
{
    private readonly Lock _gate = new();
    private Thread? _thread;
    private WidgetWindow? _window;

    // Startup-ready handshake: set by the widget thread once its controller window exists (or, as a
    // backstop, when the thread exits), so Stop never signals quit before there is a window to hear it.
    private ManualResetEventSlim? _ready;
    private WidgetRenderState _latestState = new() { Chips = [] };
    private WidgetMode _effectiveMode = WidgetMode.Hidden;
    private bool _disposed;

    /// <summary>Raised when the widget is left-clicked with an anchor rectangle and optional provider key.</summary>
    public event Action<Rectangle, string?>? Clicked;

    /// <summary>Raised when the widget is right-clicked.</summary>
    public event Action? RightClicked;

    /// <summary>Raised when the effective widget mode changes.</summary>
    public event Action<WidgetMode, string>? ModeChanged;

    /// <summary>Gets the mode currently in effect after probing and fallback.</summary>
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
    public System.Drawing.Rectangle? CurrentScreenRect
    {
        get
        {
            WidgetWindow? window;
            lock (_gate)
            {
                window = _window;
            }

            var rect = window?.CurrentScreenRect ?? System.Drawing.Rectangle.Empty;
            return rect.IsEmpty ? null : rect;
        }
    }

    /// <summary>Starts the widget host thread.</summary>
    public void Start(WidgetMode mode, bool anchorLeft = false)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            ManualResetEventSlim ready = new(false);
            _ready = ready;
            _thread = new Thread(() =>
            {
                try
                {
                    WidgetWindow window = new(this, mode, anchorLeft)
                    {
                        // Signaled on the widget thread once the controller window is created (top of
                        // the message loop), so Stop can safely post the quit request.
                        ControllerReady = ready.Set,
                    };
                    lock (_gate)
                    {
                        _window = window;
                        window.SetState(_latestState);
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
                Name = "CodexWinBar.Widget",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    /// <summary>Posts a new immutable render state to the widget thread.</summary>
    public void Update(WidgetRenderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ThrowIfDisposed();
        WidgetWindow? window;
        lock (_gate)
        {
            _latestState = state;
            window = _window;
            window?.SetState(state);
        }

        if (window?.Controller is { } controller && controller != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessageW(controller, NativeMethods.WM_APP + 1, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Stops the widget host and waits up to five seconds for its thread to exit.</summary>
    public void Stop()
    {
        Thread? thread;
        ManualResetEventSlim? ready;
        lock (_gate)
        {
            thread = _thread;
            ready = _ready;
        }

        if (thread is null)
        {
            return;
        }

        // Wait briefly for the startup handshake so a Stop racing a fresh Start doesn't post the quit
        // request before the controller window exists (which would orphan the STA thread and let a
        // later Start create a second widget). Normally this is already set and returns immediately.
        try
        {
            if (ready is not null && !ready.Wait(TimeSpan.FromSeconds(5)))
            {
                WidgetLog.Write("Widget thread did not become ready within 5 seconds.");
            }
        }
        catch (ObjectDisposedException)
        {
            // A concurrent Stop finished and disposed the event; the thread is already down (or going
            // down) — fall through to the join below.
        }

        // Re-read the window under the gate: it is published by the widget thread before Run, so after
        // a successful handshake it is guaranteed to be visible here even if Start just returned.
        WidgetWindow? window;
        lock (_gate)
        {
            window = _window;
        }

        IntPtr controller = window?.Controller ?? IntPtr.Zero;
        if (controller != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessageW(controller, WidgetWindow.QuitMessage, IntPtr.Zero, IntPtr.Zero);
        }

        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            // Leave _thread/_window intact: the STA thread may still own a live widget window, and
            // clearing state here would let a later Start create a duplicate. A subsequent Stop will
            // retry the quit request and join.
            WidgetLog.Write("Widget thread did not stop within 5 seconds; keeping host state so Start cannot create a duplicate widget.");
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_thread, thread))
            {
                _thread = null;
                _window = null;
                _ready = null;
                _effectiveMode = WidgetMode.Hidden;
            }
        }

        // The thread has exited (its backstop already set the event), so disposal cannot race a Set.
        ready?.Dispose();
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

    internal void SetEffectiveModeFromThread(WidgetMode mode, string reason)
    {
        lock (_gate)
        {
            _effectiveMode = mode;
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
