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
    private readonly Dictionary<WindowKey, double> previousRemaining = new();
    private readonly HashSet<string> firedKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> firedOrder = new();
    private readonly HashSet<string> depleted = new(StringComparer.Ordinal);
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
                return;
            }

            var config = this.configStore.Load();
            foreach (var state in this.store.States)
            {
                var entry = this.configStore.EntryFor(config, state.Provider);
                this.ProcessWindow(
                    state.Provider,
                    "session",
                    state.Snapshot?.Primary,
                    ResolveWindow(entry.QuotaWarnings?.Session, settings.QuotaSessionEnabled, settings.QuotaSessionThresholds));
                this.ProcessWindow(
                    state.Provider,
                    "weekly",
                    state.Snapshot?.Secondary,
                    ResolveWindow(entry.QuotaWarnings?.Weekly, settings.QuotaWeeklyEnabled, settings.QuotaWeeklyThresholds));
            }
        }
    }

    private void ProcessWindow(ProviderId provider, string slot, RateWindow? window, QuotaWarningWindow warningWindow)
    {
        if (window is null || window.IsSyntheticPlaceholder)
        {
            return;
        }

        if (warningWindow.Enabled == false)
        {
            return;
        }

        var remaining = Math.Clamp(window.RemainingPercent, 0, 100);
        var resetKey = ResetKey(window);
        var key = new WindowKey(provider, slot, resetKey);
        var hadPrevious = this.previousRemaining.TryGetValue(key, out var previous);
        this.previousRemaining[key] = remaining;

        var depletionKey = $"{provider.ConfigId()}|{slot}|depleted|{resetKey}";
        if (remaining < DepletedRemainingPercent)
        {
            if (this.MarkFired(depletionKey))
            {
                this.depleted.Add(depletionKey);
                this.tray.ShowBalloon("Quota depleted", $"{this.ProviderName(provider)} {slot} depleted.");
            }

            return;
        }

        if (this.depleted.Remove(depletionKey))
        {
            this.tray.ShowBalloon("Quota restored", $"{this.ProviderName(provider)} {slot} restored.");
        }

        if (!hadPrevious)
        {
            return;
        }

        foreach (var threshold in warningWindow.Thresholds ?? QuotaWarningWindow.DefaultThresholds)
        {
            if (previous > threshold && remaining <= threshold)
            {
                var thresholdKey = $"{provider.ConfigId()}|{slot}|{threshold}|{resetKey}";
                if (this.MarkFired(thresholdKey))
                {
                    var percent = Math.Round(remaining);
                    this.tray.ShowBalloon(
                        "Quota warning",
                        $"{this.ProviderName(provider)} {slot} at {percent:0}% - {ResetText(window)}");
                }
            }
        }
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

    private bool MarkFired(string key)
    {
        if (!this.firedKeys.Add(key))
        {
            return false;
        }

        this.firedOrder.Enqueue(key);
        while (this.firedOrder.Count > MaxFiredKeys)
        {
            _ = this.firedKeys.Remove(this.firedOrder.Dequeue());
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

    private readonly record struct WindowKey(ProviderId Provider, string Slot, string ResetKey);
}
