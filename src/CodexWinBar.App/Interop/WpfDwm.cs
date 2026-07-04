using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexWinBar.App.Interop;

/// <summary>Applies Windows 11 DWM chrome attributes to WPF windows.</summary>
public static class WpfDwm
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;

    /// <summary>Applies acrylic transient-window chrome to the flyout.</summary>
    public static void ApplyFlyoutChrome(Window window, bool dark)
    {
        ApplyChrome(window, dark, DwmsbtTransientWindow);
        window.Background = Brushes.Transparent;
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
    }

    /// <summary>Applies Mica main-window chrome to a normal WPF window.</summary>
    public static void ApplyWindowChrome(Window window, bool dark)
    {
        ApplyChrome(window, dark, DwmsbtMainWindow);
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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
