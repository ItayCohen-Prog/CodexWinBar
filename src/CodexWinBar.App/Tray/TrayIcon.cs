using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexWinBar.App.Notifications;

namespace CodexWinBar.App.Tray;

/// <summary>Windows notification-area icon backed by Shell_NotifyIcon.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 42;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmTaskbarCreated = 0x0000C;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifRespectQuietTime = 0x00000080;
    private readonly Action leftClick;
    private readonly Action openSettings;
    private readonly Action refresh;
    private readonly Action quit;
    private readonly Action<string> log;
    private readonly HwndSource messageSource;
    private readonly Icon trayIcon;
    private readonly IntPtr iconHandle;
    private readonly uint taskbarCreatedMessage;
    private bool disposed;

    /// <summary>Creates and adds the CodexWinBar tray icon.</summary>
    public TrayIcon(Action leftClick, Action openSettings, Action refresh, Action quit, Action<string> log)
    {
        this.leftClick = leftClick;
        this.openSettings = openSettings;
        this.refresh = refresh;
        this.quit = quit;
        this.log = log;
        var parameters = new HwndSourceParameters("CodexWinBar.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        this.messageSource = new HwndSource(parameters);
        this.messageSource.AddHook(this.WndProc);
        this.trayIcon = LoadTrayIcon();
        this.iconHandle = this.trayIcon.Handle;
        this.taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        this.AddIcon();
    }

    /// <summary>Shows a best-effort Windows notification-area balloon.</summary>
    public void ShowBalloon(string title, string text)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (ToastService.Show(title, text, this.log))
        {
            return;
        }

        var data = this.BaseData();
        data.uFlags = NifInfo;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NiifRespectQuietTime;
        _ = ShellNotifyIcon(NimModify, ref data);
    }

    /// <summary>Shows the tray context menu at the current cursor position.</summary>
    public void ShowContextMenu()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (!GetCursorPos(out var point))
        {
            point = new PointStruct(0, 0);
        }

        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
            HorizontalOffset = point.X,
            VerticalOffset = point.Y,
        };
        menu.Items.Add(new MenuItem { Header = "Refresh", Command = new RelayCommand(this.refresh) });
        menu.Items.Add(new MenuItem { Header = "Settings", Command = new RelayCommand(this.openSettings) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Quit", Command = new RelayCommand(this.quit) });
        _ = SetForegroundWindow(this.messageSource.Handle);
        menu.Closed += (_, _) => this.messageSource.Dispatcher.BeginInvoke(
            () => { },
            DispatcherPriority.Background);
        menu.IsOpen = true;
    }

    /// <summary>Removes the notification icon and destroys native resources.</summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        var data = this.BaseData();
        _ = ShellNotifyIcon(NimDelete, ref data);
        this.messageSource.RemoveHook(this.WndProc);
        this.messageSource.Dispose();
        this.trayIcon.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var mouseMessage = lParam.ToInt32();
            if (mouseMessage == WmLButtonUp)
            {
                this.leftClick();
                handled = true;
            }
            else if (mouseMessage == WmRButtonUp)
            {
                this.ShowContextMenu();
                handled = true;
            }
        }
        else if (msg == this.taskbarCreatedMessage || msg == WmTaskbarCreated)
        {
            this.AddIcon();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void AddIcon()
    {
        var data = this.BaseData();
        data.uFlags = NifIcon | NifMessage | NifTip;
        data.hIcon = this.iconHandle;
        data.uCallbackMessage = CallbackMessage;
        data.szTip = "CodexWinBar";
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Shell_NotifyIcon(NIM_ADD) failed.");
        }
    }

    private NotifyIconData BaseData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = this.messageSource.Handle,
        uID = 1,
    };

    private static Icon LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(static name => name.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new InvalidOperationException("Embedded tray icon resource app.ico was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded tray icon resource {resourceName} could not be opened.");
        using var baseIcon = new Icon(stream);
        return new Icon(baseIcon, new System.Drawing.Size(16, 16));
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointStruct point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PointStruct(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            action();
        }

        public void RaiseCanExecuteChanged()
        {
            this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
