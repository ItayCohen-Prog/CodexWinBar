using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using CodexWinBar.App.Flyout;
using CodexWinBar.App.Notifications;
using CodexWinBar.App.Settings;
using CodexWinBar.App.Tray;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using CodexWinBar.Providers;
using CodexWinBar.Widget;
using CoreWidgetMode = CodexWinBar.Core.Config.WidgetMode;
using WidgetHostMode = CodexWinBar.Widget.WidgetMode;

namespace CodexWinBar.App;

/// <summary>Process entry point and composition root for the Windows app shell.</summary>
public static class Program
{
    private const string MutexName = "CodexWinBar.SingleInstance";
    internal const string PipeName = "CodexWinBar.Activate";
    private static Mutex? singleInstanceMutex;

    /// <summary>Starts CodexWinBar.</summary>
    [STAThread]
    public static int Main()
    {
        singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SendActivationToPrimary();
            singleInstanceMutex.Dispose();
            return 0;
        }

        var app = new App();
        var shell = new AppShell(app);
        app.InitializeShell(shell);
        var exitCode = app.Run();
        singleInstanceMutex.ReleaseMutex();
        singleInstanceMutex.Dispose();
        return exitCode;
    }

    private static void SendActivationToPrimary()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(750);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Failed to activate existing CodexWinBar instance: {ex.Message}");
        }
    }
}

internal sealed class AppShell : IDisposable
{
    private static readonly System.Drawing.Rectangle FallbackAnchor = new(0, 0, 1, 1);
    private readonly App app;
    private readonly RollingLog log;
    private readonly ConfigStore configStore;
    private readonly UiSettingsStore uiStore;
    private readonly IReadOnlyList<ProviderDescriptor> descriptors;
    private readonly Dictionary<ProviderId, ProviderDescriptor> descriptorsById;
    private readonly IUsageStore usageStore;
    private readonly WidgetHost widgetHost;
    private readonly FlyoutWindow flyout;
    private readonly TrayIcon trayIcon;
    private readonly QuotaNotifier quotaNotifier;
    private readonly CancellationTokenSource pipeCancellation = new();
    private bool modeBalloonShown;
    private bool disposed;

    public AppShell(App app)
    {
        this.app = app;
        this.log = new RollingLog();
        this.configStore = new ConfigStore(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            this.Log);
        this.uiStore = new UiSettingsStore(this.Log);
        this.descriptors = ProviderCatalog.CreateAll();
        this.descriptorsById = this.descriptors.ToDictionary(descriptor => descriptor.Id);
        this.usageStore = new UsageStore(this.descriptors, this.configStore, this.uiStore, this.Log);
        this.widgetHost = new WidgetHost();
        WidgetHost.SetLogger(this.Log);

        this.flyout = new FlyoutWindow(
            this.usageStore,
            this.uiStore,
            this.OpenSettings,
            this.Quit,
            this.Log);
        this.trayIcon = new TrayIcon(
            () => this.ToggleFlyout(FallbackAnchor),
            this.OpenSettings,
            this.Refresh,
            this.Quit);
        this.quotaNotifier = new QuotaNotifier(this.usageStore, this.configStore, this.trayIcon);
    }

    public void Start()
    {
        this.InstallExceptionHandlers();
        this.StartActivationPipe();
        this.WireEvents();
        this.usageStore.Start();
        this.StartWidgetFromSettings();
        this.UpdateWidget();
        this.Log($"CodexWinBar {Assembly.GetExecutingAssembly().GetName().Version} started.");
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.pipeCancellation.Cancel();
        this.quotaNotifier.Dispose();
        this.trayIcon.Dispose();
        this.widgetHost.Dispose();
        this.usageStore.Dispose();
        this.pipeCancellation.Dispose();
        this.log.Dispose();
    }

    private void WireEvents()
    {
        this.widgetHost.Clicked += rect => this.app.Dispatcher.BeginInvoke(() =>
        {
            this.Log("widget Clicked event received");
            this.ToggleFlyout(rect);
        });
        this.widgetHost.RightClicked += () => this.app.Dispatcher.BeginInvoke(() => this.trayIcon.ShowContextMenu());
        this.widgetHost.ModeChanged += (mode, reason) =>
        {
            this.Log($"Widget mode changed to {mode}: {reason}");
            if (!this.modeBalloonShown)
            {
                this.modeBalloonShown = true;
                this.app.Dispatcher.BeginInvoke(() => this.trayIcon.ShowBalloon("CodexWinBar", $"Widget mode: {mode}"));
            }
        };
        this.usageStore.StateChanged += () => this.app.Dispatcher.BeginInvoke(this.UpdateWidget);
    }

    private void StartWidgetFromSettings()
    {
        var settings = this.uiStore.Load();
        this.widgetHost.Start(ToWidgetMode(settings.WidgetMode));
    }

    private void ToggleFlyout(System.Drawing.Rectangle anchorPhysicalPx)
    {
        if (this.flyout.IsOpen)
        {
            this.flyout.Toggle(anchorPhysicalPx);
            return;
        }

        this.usageStore.NotifyFlyoutOpened();
        this.flyout.Toggle(anchorPhysicalPx);
    }

    private void OpenSettings()
    {
        SettingsWindow.ShowOrActivate(this.configStore, this.uiStore, this.usageStore, this.ApplySettings);
    }

    private void ApplySettings()
    {
        this.UpdateWidget();
    }

    private void Refresh()
    {
        _ = this.usageStore.RefreshAllAsync();
    }

    private void Quit()
    {
        this.app.Shutdown();
    }

    private void UpdateWidget()
    {
        var settings = this.uiStore.Load();
        var chips = this.usageStore.States.Select(state => this.ToChipState(state, settings)).ToArray();
        this.widgetHost.Update(new WidgetRenderState
        {
            Chips = chips,
            IsLoading = this.usageStore.States.Any(state => state.IsRefreshing),
        });
    }

    private WidgetChipState ToChipState(ProviderState state, UiSettings settings)
    {
        var descriptor = this.descriptorsById[state.Provider];
        var primary = state.Snapshot?.Primary;
        var secondary = state.Snapshot?.Secondary;
        var sessionPercent = WindowPercent(primary, settings.UsageBarsShowUsed);
        var weeklyPercent = WindowPercent(secondary, settings.UsageBarsShowUsed);
        return new WidgetChipState
        {
            ProviderKey = state.Provider.ConfigId(),
            GlyphKey = descriptor.Branding.GlyphKey,
            BrandR = descriptor.Branding.R,
            BrandG = descriptor.Branding.G,
            BrandB = descriptor.Branding.B,
            SessionPercent = sessionPercent,
            WeeklyPercent = weeklyPercent,
            Text = WidgetText(primary, secondary, settings),
            IsStale = state.IsStale,
            IncidentLevel = IncidentLevel(state.ServiceStatus),
            Tooltip = Tooltip(descriptor, state, settings),
        };
    }

    private void StartActivationPipe()
    {
        _ = Task.Run(async () =>
        {
            while (!this.pipeCancellation.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        Program.PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(this.pipeCancellation.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(pipe);
                    var line = await reader.ReadLineAsync(this.pipeCancellation.Token).ConfigureAwait(false);
                    if (string.Equals(line, "show", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = this.app.Dispatcher.BeginInvoke(() => this.ToggleFlyout(FallbackAnchor));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    this.Log($"Activation pipe error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });
    }

    private void InstallExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                this.Log($"Unhandled exception: {ex}");
            }
        };
        this.app.DispatcherUnhandledException += (_, args) =>
        {
            this.Log($"Dispatcher exception: {args.Exception}");
            args.Handled = true;
        };
    }

    private void Log(string message)
    {
        this.log.Write(message);
    }

    private static WidgetHostMode ToWidgetMode(CoreWidgetMode mode) => mode switch
    {
        CoreWidgetMode.Embedded => WidgetHostMode.Embedded,
        CoreWidgetMode.Overlay => WidgetHostMode.Overlay,
        CoreWidgetMode.Hidden => WidgetHostMode.Hidden,
        _ => WidgetHostMode.Auto,
    };

    private static double? WindowPercent(RateWindow? window, bool showUsed)
    {
        if (window is null)
        {
            return null;
        }

        var value = showUsed ? window.UsedPercent : window.RemainingPercent;
        return Math.Clamp(value, 0, 100);
    }

    private static string? WidgetText(RateWindow? primary, RateWindow? secondary, UiSettings settings)
    {
        if (primary is null && secondary is null)
        {
            return null;
        }

        return settings.DisplayTextMode switch
        {
            DisplayTextMode.ResetTime => ResetText(primary?.ResetsAt ?? secondary?.ResetsAt, settings.ResetTimesShowAbsolute),
            DisplayTextMode.Pace => PercentText(primary, settings.UsageBarsShowUsed),
            DisplayTextMode.Both => Combine(PercentText(primary, settings.UsageBarsShowUsed), WeeklyText(secondary, settings.UsageBarsShowUsed)),
            _ => Combine(PercentText(primary, settings.UsageBarsShowUsed), WeeklyText(secondary, settings.UsageBarsShowUsed)),
        };
    }

    private static string? PercentText(RateWindow? window, bool showUsed)
    {
        var percent = WindowPercent(window, showUsed);
        return percent is null ? null : $"{percent.Value:0}%";
    }

    private static string? WeeklyText(RateWindow? window, bool showUsed)
    {
        var percent = WindowPercent(window, showUsed);
        return percent is null ? null : $"W {percent.Value:0}%";
    }

    private static string? ResetText(DateTimeOffset? resetsAt, bool absolute)
    {
        if (resetsAt is null)
        {
            return null;
        }

        if (absolute)
        {
            return resetsAt.Value.LocalDateTime.ToShortTimeString();
        }

        var remaining = resetsAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m"
            : $"{Math.Max(1, remaining.Minutes)}m";
    }

    private static string? Combine(string? primary, string? secondary)
    {
        return (primary, secondary) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{primary} · {secondary}",
            ({ Length: > 0 }, _) => primary,
            (_, { Length: > 0 }) => secondary,
            _ => null,
        };
    }

    private static int IncidentLevel(ProviderStatus? status) => status?.Indicator switch
    {
        StatusIndicator.Minor or StatusIndicator.Maintenance => 1,
        StatusIndicator.Major or StatusIndicator.Critical or StatusIndicator.Unknown => 2,
        _ => 0,
    };

    private static string Tooltip(ProviderDescriptor descriptor, ProviderState state, UiSettings settings)
    {
        var text = WidgetText(state.Snapshot?.Primary, state.Snapshot?.Secondary, settings);
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            return $"{descriptor.Metadata.DisplayName}: {state.Error}";
        }

        return string.IsNullOrWhiteSpace(text)
            ? descriptor.Metadata.DisplayName
            : $"{descriptor.Metadata.DisplayName}: {text}";
    }
}

internal sealed class RollingLog : IDisposable
{
    private const long MaxBytes = 1_000_000;
    private readonly string path;
    private readonly object gate = new();

    public RollingLog()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(local, "CodexWinBar", "logs");
        Directory.CreateDirectory(directory);
        this.path = Path.Combine(directory, "app.log");
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        Debug.WriteLine(line);
        lock (this.gate)
        {
            this.TrimIfNeeded();
            File.AppendAllText(this.path, line + Environment.NewLine);
        }
    }

    public void Dispose()
    {
    }

    private void TrimIfNeeded()
    {
        var file = new FileInfo(this.path);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        var oldPath = this.path + ".old";
        if (File.Exists(oldPath))
        {
            File.Delete(oldPath);
        }

        File.Move(this.path, oldPath);
    }
}
