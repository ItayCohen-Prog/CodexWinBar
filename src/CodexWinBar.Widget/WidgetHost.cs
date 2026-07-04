using System.Drawing;
using System.Threading;

namespace CodexWinBar.Widget;

/// <summary>Hosts the CodexWinBar taskbar widget on a dedicated STA thread.</summary>
public sealed class WidgetHost : IWidgetHost
{
    private readonly Lock _gate = new();
    private Thread? _thread;
    private WidgetWindow? _window;
    private WidgetRenderState _latestState = new() { Chips = [] };
    private WidgetMode _effectiveMode = WidgetMode.Hidden;
    private bool _disposed;

    /// <summary>Raised when the widget is left-clicked with its current screen rectangle.</summary>
    public event Action<Rectangle>? Clicked;

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

    /// <summary>Starts the widget host thread.</summary>
    public void Start(WidgetMode mode)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(() =>
            {
                WidgetWindow window = new(this, mode);
                lock (_gate)
                {
                    _window = window;
                    window.SetState(_latestState);
                }

                window.Run();
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
        WidgetWindow? window;
        lock (_gate)
        {
            thread = _thread;
            window = _window;
        }

        if (thread is null)
        {
            return;
        }

        IntPtr controller = window?.Controller ?? IntPtr.Zero;
        if (controller != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessageW(controller, NativeMethods.WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }

        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            WidgetLog.Write("Widget thread did not stop within 5 seconds.");
        }

        lock (_gate)
        {
            _thread = null;
            _window = null;
            _effectiveMode = WidgetMode.Hidden;
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

    internal void SetEffectiveModeFromThread(WidgetMode mode, string reason)
    {
        lock (_gate)
        {
            _effectiveMode = mode;
        }

        ModeChanged?.Invoke(mode, reason);
    }

    internal void RaiseClicked(Rectangle rect)
    {
        Clicked?.Invoke(rect);
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
