using CodexWinBar.App.Tray;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using CodexWinBar.Providers;

namespace CodexWinBar.App.Notifications;

/// <summary>
/// Dispatches pace warning tray balloons when a provider window's projected end-of-window usage
/// crosses into a concerning band (at-risk of running out early, or under-using the quota).
/// </summary>
public sealed class PaceNotifier : IDisposable
{
    private const int MaxFiredKeys = 256;

    private readonly IUsageStore store;
    private readonly UiSettingsStore uiStore;
    private readonly TrayIcon tray;
    private readonly IReadOnlyDictionary<ProviderId, string> names;
    private readonly object gate = new();
    private readonly ResetWindowHistory<PaceState> history = new(MaxFiredKeys);
    private bool disposed;

    /// <summary>
    /// Initializes a notifier bound to usage-store state changes. The config store parameter is
    /// accepted for wiring parity with <see cref="QuotaNotifier"/>; pace bands currently have no
    /// per-provider overrides, so it is unused.
    /// </summary>
    public PaceNotifier(IUsageStore store, ConfigStore cfg, UiSettingsStore uiStore, TrayIcon tray)
    {
        this.store = store;
        this.uiStore = uiStore;
        this.tray = tray;
        this.names = ProviderCatalog.CreateAll().ToDictionary(item => item.Id, item => item.Metadata.DisplayName);
        this.store.StateChanged += this.OnStateChanged;
    }

    /// <summary>
    /// Stops listening for usage state changes.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.store.StateChanged -= this.OnStateChanged;
    }

    private void OnStateChanged()
    {
        lock (this.gate)
        {
            var settings = this.uiStore.Load();
            if (!settings.PaceNotificationsEnabled)
            {
                this.history.Clear();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var activeSlots = new HashSet<NotificationSlot>();
            foreach (var state in this.store.States)
            {
                if (this.ProcessWindow(state.Provider, "session", state.Snapshot?.Primary, settings.PaceUnderuseNotificationsEnabled, now))
                {
                    activeSlots.Add(new NotificationSlot(state.Provider, "session"));
                }

                if (this.ProcessWindow(state.Provider, "weekly", state.Snapshot?.Secondary, settings.PaceUnderuseNotificationsEnabled, now))
                {
                    activeSlots.Add(new NotificationSlot(state.Provider, "weekly"));
                }
            }

            this.history.RetainOnly(activeSlots);
        }
    }

    private bool ProcessWindow(ProviderId provider, string slot, RateWindow? window, bool underuseEnabled, DateTimeOffset now)
    {
        var notificationSlot = new NotificationSlot(provider, slot);
        if (window is null || window.IsSyntheticPlaceholder)
        {
            this.history.Forget(notificationSlot);
            return false;
        }

        var resetKey = ResetKey(window);
        if (PaceCalculator.Compute(window, now) is not { } pace)
        {
            this.history.Forget(notificationSlot);
            return false;
        }

        var observation = this.history.Observe(notificationSlot, resetKey, pace.State);

        if (!observation.HadPrevious || observation.Previous == pace.State)
        {
            return true;
        }

        if (pace.State == PaceState.AtRisk)
        {
            if (this.history.TryMarkFired(notificationSlot, resetKey, "at-risk"))
            {
                this.tray.ShowBalloon(
                    "Pace warning",
                    $"{this.ProviderName(provider)} {slot}: on pace to run out before it resets (~{pace.ProjectedPercent:0}%) - {ResetText(window)}.");
            }
        }
        else if (pace.State == PaceState.Underusing && underuseEnabled)
        {
            if (this.history.TryMarkFired(notificationSlot, resetKey, "underuse"))
            {
                this.tray.ShowBalloon(
                    "Pace notice",
                    $"{this.ProviderName(provider)} {slot}: under-using - lots of quota left with time to spare.");
            }
        }

        return true;
    }

    private string ProviderName(ProviderId provider) =>
        this.names.TryGetValue(provider, out var name) ? name : provider.ToString();

    private static string ResetKey(RateWindow window) =>
        window.ResetsAt?.ToUniversalTime().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? window.ResetDescription
        ?? "unknown";

    private static string ResetText(RateWindow window)
    {
        if (window.ResetsAt is { } resetsAt)
        {
            var remaining = resetsAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "reset is due";
            }

            if (remaining.TotalHours >= 1)
            {
                return $"resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
            }

            return $"resets in {Math.Max(1, remaining.Minutes)}m";
        }

        return string.IsNullOrWhiteSpace(window.ResetDescription) ? "reset time unknown" : window.ResetDescription;
    }
}
