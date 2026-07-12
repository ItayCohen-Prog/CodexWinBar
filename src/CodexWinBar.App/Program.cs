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
using Velopack;
using Velopack.Sources;
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
    public static int Main(string[] args)
    {
        // MUST run before anything else (including the single-instance mutex and WPF): during install,
        // update and uninstall Velopack relaunches this exe with hook arguments, and VelopackApp handles
        // them (shortcuts, etc.) and exits. Building an installer without this leaves the app un-updatable.
        VelopackApp.Build().Run();

        if (args.Contains("--test-notification", StringComparer.OrdinalIgnoreCase))
        {
            ToastService.Initialize(message => Debug.WriteLine(message));
            _ = ToastService.Show(
                "Test notification",
                "CodexWinBar notifications are working.",
                message => Debug.WriteLine(message));
            Thread.Sleep(3000);
            return 0;
        }

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
    /// <summary>
    /// Anchor used when the widget has no on-screen rect (starting/hidden). The huge Y lets the
    /// flyout's work-area clamping resolve it to the bottom-left corner on any monitor/DPI.
    /// </summary>
    private static readonly System.Drawing.Rectangle FallbackAnchor = new(0, 1_000_000, 1, 1);
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
    private readonly PaceNotifier paceNotifier;
    private readonly CancellationTokenSource pipeCancellation = new();
    private readonly Action usageStateChangedHandler;
    // Providers the user has already opened while at-risk; suppresses the bob until they drop out of the
    // at-risk band and re-enter it (so it nudges once per episode rather than nagging every refresh).
    private readonly HashSet<string> acknowledgedRisk = new(StringComparer.OrdinalIgnoreCase);
    private WidgetHostMode? lastWidgetModeRequest;
    private WidgetSide? lastWidgetSide;
    private WidgetRenderState lastWidgetState = new() { Chips = [] };
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
        this.usageStore = Dev.FakeUsageStore.IsEnabled(Environment.GetEnvironmentVariable)
            ? new Dev.FakeUsageStore(this.Log)
            : new UsageStore(this.descriptors, this.configStore, this.uiStore, this.Log);
        this.widgetHost = new WidgetHost();
        WidgetHost.SetLogger(this.Log);
        ToastService.Initialize(this.Log);

        this.flyout = new FlyoutWindow(
            this.usageStore,
            this.uiStore,
            this.OpenSettings,
            this.Quit,
            this.Log);
        this.trayIcon = new TrayIcon(
            () => this.ToggleFlyout(this.GetActivationAnchor()),
            this.OpenSettings,
            this.Refresh,
            this.Quit,
            this.Log);
        this.quotaNotifier = new QuotaNotifier(this.usageStore, this.configStore, this.uiStore, this.trayIcon);
        this.paceNotifier = new PaceNotifier(this.usageStore, this.configStore, this.uiStore, this.trayIcon);
        this.usageStateChangedHandler = () => this.app.Dispatcher.BeginInvoke(() => this.UpdateWidget());
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
        this.CheckForUpdatesInBackground();
        // Warm the flyout's render/animation pipeline once the app is idle, so the FIRST real open
        // animates instead of popping in (WPF drops the first animation on a cold composition).
        _ = this.app.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                this.flyout.PreWarm();
                this.ShowOnboardingIfFirstRun();
            }));
    }

    // Fire-and-forget update check. Velopack only manages updates for an installed copy (IsInstalled
    // guards dev/portable builds). Updates are downloaded and STAGED — applied on the next exit — so the
    // running session is never interrupted; the user gets the new version the next time they launch.
    private void CheckForUpdatesInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var manager = new UpdateManager(
                    new GithubSource("https://github.com/ItayCohen-Prog/CodexWinBar", accessToken: null, prerelease: false));
                if (!manager.IsInstalled)
                {
                    return;
                }

                var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (update is null)
                {
                    return;
                }

                await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
                manager.WaitExitThenApplyUpdates(update);
                this.Log("Update downloaded; it will be applied the next time CodexWinBar restarts.");
            }
            catch (Exception ex)
            {
                this.Log($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.pipeCancellation.Cancel();
        this.usageStore.StateChanged -= this.usageStateChangedHandler;
        this.usageStore.Dispose();
        this.quotaNotifier.Dispose();
        this.paceNotifier.Dispose();
        this.trayIcon.Dispose();
        this.widgetHost.Dispose();
        this.pipeCancellation.Dispose();
        this.log.Dispose();
    }

    private void WireEvents()
    {
        this.widgetHost.Clicked += (rect, providerKey) => this.app.Dispatcher.BeginInvoke(() =>
        {
            this.Log("widget Clicked event received");
            this.ToggleFlyout(rect, ProviderIdFromKey(providerKey));
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
        this.usageStore.StateChanged += this.usageStateChangedHandler;
    }

    private void StartWidgetFromSettings()
    {
        var settings = this.uiStore.Load();
        var mode = ResolveWidgetMode(settings);
        var anchorLeft = settings.WidgetSide == WidgetSide.Left;
        this.lastWidgetModeRequest = mode;
        this.lastWidgetSide = settings.WidgetSide;
        this.widgetHost.Start(mode, anchorLeft);
    }

    /// <summary>Best flyout anchor available right now: the live widget rect, else bottom-left fallback.</summary>
    private System.Drawing.Rectangle GetActivationAnchor() =>
        this.widgetHost.CurrentScreenRect ?? FallbackAnchor;

    private void ToggleFlyout(System.Drawing.Rectangle anchorPhysicalPx, ProviderId? focusProvider = null)
    {
        // Opening a provider acknowledges its at-risk nudge, so its chip stops bobbing.
        if (focusProvider is { } focus && this.acknowledgedRisk.Add(focus.ConfigId()))
        {
            this.UpdateWidget();
        }

        if (this.flyout.IsOpen)
        {
            this.flyout.Toggle(anchorPhysicalPx, focusProvider);
            return;
        }

        this.usageStore.NotifyFlyoutOpened();
        this.flyout.Toggle(anchorPhysicalPx, focusProvider);
    }

    private static ProviderId? ProviderIdFromKey(string? providerKey) =>
        string.IsNullOrWhiteSpace(providerKey) ? null : ProviderIds.TryParse(providerKey);

    private void OpenSettings()
    {
        SettingsWindow.ShowOrActivate(this.configStore, this.uiStore, this.usageStore, this.ApplySettings);
    }

    // First launch (no onboarding recorded yet) opens the connect-your-providers welcome. It marks
    // itself complete on close, so it appears once. Providers stay disconnected until the user signs in.
    private void ShowOnboardingIfFirstRun()
    {
        if (this.uiStore.Load().OnboardingCompleted)
        {
            return;
        }

        OnboardingWindow.Show(
            this.descriptors,
            this.configStore,
            this.uiStore,
            this.usageStore,
            this.ApplySettings);
    }

    private void ApplySettings()
    {
        var settings = this.uiStore.Load();
        var mode = ResolveWidgetMode(settings);
        if (this.lastWidgetModeRequest != mode || this.lastWidgetSide != settings.WidgetSide)
        {
            this.widgetHost.Stop();
            this.widgetHost.Start(mode, settings.WidgetSide == WidgetSide.Left);
            this.lastWidgetModeRequest = mode;
            this.lastWidgetSide = settings.WidgetSide;
            this.widgetHost.Update(this.lastWidgetState);
        }

        this.usageStore.ReloadSchedule();
        this.UpdateWidget(settings);
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
        this.UpdateWidget(settings);
    }

    private void UpdateWidget(UiSettings settings)
    {
        var states = this.usageStore.States;
        var chips = states.Select(state => this.ToChipState(state, settings)).ToArray();
        this.ApplyAttention(chips, states);
        this.lastWidgetState = new WidgetRenderState
        {
            Chips = chips,
            IsLoading = this.usageStore.States.Any(state => state.IsRefreshing),
        };
        this.widgetHost.Update(this.lastWidgetState);
    }

    // Flags the single most at-risk provider so its chip bobs — the one on pace to run out SOONEST
    // (combining burn rate and how little is left), unless the user has already opened it this episode.
    // Acknowledgements for providers no longer at-risk are dropped so a fresh spike bobs again.
    private void ApplyAttention(WidgetChipState[] chips, IReadOnlyList<ProviderState> states)
    {
        var atRisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chip in chips)
        {
            if (chip.PaceBand == 2)
            {
                atRisk.Add(chip.ProviderKey);
            }
        }

        this.acknowledgedRisk.IntersectWith(atRisk);

        int target = -1;
        double soonest = double.PositiveInfinity;
        for (int i = 0; i < chips.Length; i++)
        {
            var chip = chips[i];
            if (chip.PaceBand != 2 || this.acknowledgedRisk.Contains(chip.ProviderKey))
            {
                continue;
            }

            var window = states[i].Snapshot?.Primary ?? states[i].Snapshot?.Secondary;
            double minutes = MinutesToExhaustion(window) ?? double.MaxValue;
            if (minutes < soonest)
            {
                soonest = minutes;
                target = i;
            }
        }

        if (target >= 0)
        {
            chips[target] = chips[target] with { Attention = true };
        }
    }

    // Projected minutes until a window hits 100% at the current burn rate; null when unknowable.
    // Lower = more urgent. Folds together burn rate (used ÷ elapsed) and how much quota is left.
    private static double? MinutesToExhaustion(RateWindow? window)
    {
        if (window is null || window.WindowMinutes is not { } minutes || minutes <= 0 ||
            window.ResetsAt is not { } resetsAt || window.UsedPercent <= 0)
        {
            return null;
        }

        var elapsedFraction = 1.0 - ((resetsAt - DateTimeOffset.UtcNow).TotalMinutes / minutes);
        if (elapsedFraction <= 0.001)
        {
            return null;
        }

        var burnPerMinute = window.UsedPercent / (minutes * elapsedFraction);
        if (burnPerMinute <= 0)
        {
            return null;
        }

        return Math.Max(0, (100 - window.UsedPercent) / burnPerMinute);
    }

    private WidgetChipState ToChipState(ProviderState state, UiSettings settings)
    {
        var descriptor = this.descriptorsById[state.Provider];
        var primary = state.Snapshot?.Primary;
        var secondary = state.Snapshot?.Secondary;
        var sessionPercent = WindowPercent(primary, settings.UsageBarsShowUsed);
        var weeklyPercent = WindowPercent(secondary, settings.UsageBarsShowUsed);
        var (paceBand, paceProjected) = ChipPace(primary, secondary, settings);
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
            PaceBand = paceBand,
            PaceProjectedPercent = paceProjected,
            Tooltip = Tooltip(descriptor, state, settings),
        };
    }

    // Pace for the chip is taken from the session window (falling back to weekly). Returns band
    // (-1 none, 0 under-using, 1 on-track, 2 at-risk) and the projected end-of-window usage.
    private static (int Band, double Projected) ChipPace(RateWindow? primary, RateWindow? secondary, UiSettings settings)
    {
        if (!settings.ShowPaceIndicator)
        {
            return (-1, 0);
        }

        var window = primary ?? secondary;
        if (window is null || PaceCalculator.Compute(window, DateTimeOffset.UtcNow) is not { } pace)
        {
            return (-1, 0);
        }

        return ((int)pace.State, pace.ProjectedPercent);
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
                    if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("show", StringComparison.OrdinalIgnoreCase))
                    {
                        // "show" opens the all-providers view; "show:<configId>" focuses one provider,
                        // which also drives a live provider switch when the flyout is already open.
                        var colon = line.IndexOf(':');
                        var focus = colon >= 0 ? ProviderIds.TryParse(line[(colon + 1)..].Trim()) : null;
                        _ = this.app.Dispatcher.BeginInvoke(() => this.ToggleFlyout(this.GetActivationAnchor(), focus));
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

    // The left side of a centered Windows 11 taskbar is occupied by the system's own Widgets/weather
    // button, so an embedded strip there would collide — the left placement floats as an overlay instead.
    private static WidgetHostMode ResolveWidgetMode(UiSettings settings) =>
        settings.WidgetSide == WidgetSide.Left ? WidgetHostMode.Overlay : ToWidgetMode(settings.WidgetMode);

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
            DisplayTextMode.Pace => PaceText(primary, settings.UsageBarsShowUsed),
            DisplayTextMode.Both => Combine(PercentText(primary, settings.UsageBarsShowUsed), WeeklyText(secondary, settings.UsageBarsShowUsed)),
            _ => PercentText(primary, settings.UsageBarsShowUsed),
        };
    }

    private static string? PaceText(RateWindow? window, bool showUsed)
    {
        if (window is not null && PaceCalculator.Compute(window, DateTimeOffset.UtcNow) is { } pace)
        {
            return $"→{pace.ProjectedPercent:0}%";
        }

        return PercentText(window, showUsed);
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
