using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CodexWinBar.App.Notifications;

/// <summary>WinRT toast notifications for unpackaged desktop app activation.</summary>
internal static class ToastService
{
    public const string Aumid = "ItayCohen.CodexWinBar";

    public static void Initialize(Action<string> log)
    {
        try
        {
            var iconPath = EnsureIconFile(log);
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
            key?.SetValue("DisplayName", "CodexWinBar", RegistryValueKind.String);
            key?.SetValue("IconUri", iconPath, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            log($"Toast registration failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var result = SetCurrentProcessExplicitAppUserModelID(Aumid);
            if (result != 0)
            {
                log($"SetCurrentProcessExplicitAppUserModelID failed: HRESULT 0x{result:X8}");
            }
        }
        catch (Exception ex)
        {
            log($"SetCurrentProcessExplicitAppUserModelID failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool Show(string title, string message, Action<string> log)
    {
        try
        {
            var escapedTitle = SecurityElement.Escape(title) ?? string.Empty;
            var escapedMessage = SecurityElement.Escape(message) ?? string.Empty;
            var xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{escapedTitle}</text>
                      <text>{escapedMessage}</text>
                    </binding>
                  </visual>
                </toast>
                """;
            var document = new XmlDocument();
            document.LoadXml(xml);
            var notification = new ToastNotification(document);
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            log($"Toast notification failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string EnsureIconFile(Action<string> log)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(local, "CodexWinBar");
        Directory.CreateDirectory(directory);
        var iconPath = Path.Combine(directory, "app.ico");
        if (File.Exists(iconPath))
        {
            return iconPath;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(static name => name.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            log("Embedded toast icon resource app.ico was not found.");
            return iconPath;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            log($"Embedded toast icon resource {resourceName} could not be opened.");
            return iconPath;
        }

        using var file = File.Create(iconPath);
        stream.CopyTo(file);
        return iconPath;
    }

    [DllImport("shell32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
