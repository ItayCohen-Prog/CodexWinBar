using CodexWinBar.App.Tray;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using CodexWinBar.Providers;

namespace CodexWinBar.App.Notifications;

/// <summary>
/// Dispatches quota warning tray balloons when provider usage crosses configured thresholds.
/// </summary>
public sealed class QuotaNotifier : IDisposable
{
    private const int MaxFiredKeys = 256;
    private const double DepletedRemainingPercent = 0.01;

    private readonly IUsageStore store;
    private readonly ConfigStore configStore;
    private readonly UiSettingsStore uiStore;
    private readonly TrayIcon tray;
    private readonly IReadOnlyDictionary<ProviderId, string> names;
    private readonly object gate = new();
    private readonly ResetWindowHistory<double> history = new(MaxFiredKeys);
    private readonly HashSet<NotificationSlot> depleted = [];
    private bool disposed;

    /// <summary>
    /// Initializes a notifier bound to usage-store state changes.
    /// </summary>
    public QuotaNotifier(IUsageStore store, ConfigStore cfg, UiSettingsStore uiStore, TrayIcon tray)
    {
        this.store = store;
        this.configStore = cfg;
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
            if (!settings.QuotaNotificationsEnabled)
            {
                this.history.Clear();
                this.depleted.Clear();
                return;
            }

            var config = this.configStore.Load();
            var activeSlots = new HashSet<NotificationSlot>();
            foreach (var state in this.store.States)
            {
                var entry = this.configStore.EntryFor(config, state.Provider);
                if (this.ProcessWindow(
                    state.Provider,
                    "session",
                    state.Snapshot?.Primary,
                    ResolveWindow(entry.QuotaWarnings?.Session, settings.QuotaSessionEnabled, settings.QuotaSessionThresholds)))
                {
                    activeSlots.Add(new NotificationSlot(state.Provider, "session"));
                }

                if (this.ProcessWindow(
                    state.Provider,
                    "weekly",
                    state.Snapshot?.Secondary,
                    ResolveWindow(entry.QuotaWarnings?.Weekly, settings.QuotaWeeklyEnabled, settings.QuotaWeeklyThresholds)))
                {
                    activeSlots.Add(new NotificationSlot(state.Provider, "weekly"));
                }
            }

            this.history.RetainOnly(activeSlots);
            this.depleted.IntersectWith(activeSlots);
        }
    }

    private bool ProcessWindow(ProviderId provider, string slot, RateWindow? window, QuotaWarningWindow warningWindow)
    {
        var notificationSlot = new NotificationSlot(provider, slot);
        if (window is null || window.IsSyntheticPlaceholder)
        {
            this.history.Forget(notificationSlot);
            _ = this.depleted.Remove(notificationSlot);
            return false;
        }

        if (warningWindow.Enabled == false)
        {
            this.history.Forget(notificationSlot);
            _ = this.depleted.Remove(notificationSlot);
            return false;
        }

        var remaining = Math.Clamp(window.RemainingPercent, 0, 100);
        var resetKey = ResetKey(window);
        var observation = this.history.Observe(notificationSlot, resetKey, remaining);
        if (observation.ResetChanged)
        {
            _ = this.depleted.Remove(notificationSlot);
            if (observation.PreviousWasNotified)
            {
                this.tray.ShowBalloon("Quota restored", $"{this.ProviderName(provider)} {slot} restored.");
            }
        }

        if (remaining < DepletedRemainingPercent)
        {
            if (!this.depleted.Contains(notificationSlot) &&
                this.history.TryMarkFired(notificationSlot, resetKey, "depleted", marksWindowNotified: true))
            {
                _ = this.depleted.Add(notificationSlot);
                this.tray.ShowBalloon("Quota depleted", $"{this.ProviderName(provider)} {slot} depleted.");
            }

            return true;
        }

        if (this.depleted.Remove(notificationSlot))
        {
            this.history.ClearWindowNotification(notificationSlot, resetKey);
            this.tray.ShowBalloon("Quota restored", $"{this.ProviderName(provider)} {slot} restored.");
        }

        if (!observation.HadPrevious)
        {
            return true;
        }

        foreach (var threshold in warningWindow.Thresholds ?? QuotaWarningWindow.DefaultThresholds)
        {
            if (observation.Previous > threshold && remaining <= threshold)
            {
                if (this.history.TryMarkFired(notificationSlot, resetKey, $"threshold:{threshold}"))
                {
                    var percent = Math.Round(remaining);
                    this.tray.ShowBalloon(
                        "Quota warning",
                        $"{this.ProviderName(provider)} {slot} at {percent:0}% - {ResetText(window)}");
                }
            }
        }

        return true;
    }

    private static QuotaWarningWindow ResolveWindow(
        QuotaWarningWindow? providerWindow,
        bool globalEnabled,
        IReadOnlyList<int> globalThresholds)
    {
        if (providerWindow is not null)
        {
            return ConfigStore.Normalize(providerWindow);
        }

        return new QuotaWarningWindow
        {
            Enabled = globalEnabled,
            Thresholds = NormalizeThresholds(globalThresholds),
        };
    }

    private static IReadOnlyList<int> NormalizeThresholds(IReadOnlyList<int> thresholds) =>
        thresholds
            .Select(threshold => Math.Clamp(threshold, 0, 99))
            .Distinct()
            .OrderDescending()
            .ToArray();

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
