using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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
    private readonly HwndSource messageSource;
    private readonly IntPtr iconHandle;
    private readonly uint taskbarCreatedMessage;
    private bool disposed;

    /// <summary>Creates and adds the CodexWinBar tray icon.</summary>
    public TrayIcon(Action leftClick, Action openSettings, Action refresh, Action quit)
    {
        this.leftClick = leftClick;
        this.openSettings = openSettings;
        this.refresh = refresh;
        this.quit = quit;
        var parameters = new HwndSourceParameters("CodexWinBar.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        this.messageSource = new HwndSource(parameters);
        this.messageSource.AddHook(this.WndProc);
        this.iconHandle = CreateTrayIcon();
        this.taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        this.AddIcon();
    }

    /// <summary>Shows a best-effort Windows notification-area balloon.</summary>
    public void ShowBalloon(string title, string text)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
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
        if (this.iconHandle != IntPtr.Zero)
        {
            _ = DestroyIcon(this.iconHandle);
        }
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

    private static IntPtr CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            using var accent = new SolidBrush(System.Drawing.Color.FromArgb(255, 16, 163, 127));
            using var white = new System.Drawing.Pen(System.Drawing.Color.White, 3.5f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            using var path = RoundedRectangle(4, 4, 24, 24, 7);
            graphics.FillPath(accent, path);
            graphics.DrawArc(white, 10, 11, 12, 12, 200, 140);
            graphics.DrawLine(white, 16, 17, 21, 12);
        }

        var iconInfo = new IconInfo();
        var color = bitmap.GetHbitmap(System.Drawing.Color.FromArgb(0));
        var mask = bitmap.GetHbitmap(System.Drawing.Color.Black);
        try
        {
            iconInfo.fIcon = true;
            iconInfo.xHotspot = 0;
            iconInfo.yHotspot = 0;
            iconInfo.hbmColor = color;
            iconInfo.hbmMask = mask;
            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            _ = DeleteObject(color);
            _ = DeleteObject(mask);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

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
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
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
