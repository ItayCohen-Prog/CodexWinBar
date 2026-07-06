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
    private readonly Dictionary<WindowKey, PaceState> previousBand = new();
    private readonly HashSet<string> firedKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> firedOrder = new();
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
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var state in this.store.States)
            {
                this.ProcessWindow(state.Provider, "session", state.Snapshot?.Primary, settings.PaceUnderuseNotificationsEnabled, now);
                this.ProcessWindow(state.Provider, "weekly", state.Snapshot?.Secondary, settings.PaceUnderuseNotificationsEnabled, now);
            }
        }
    }

    private void ProcessWindow(ProviderId provider, string slot, RateWindow? window, bool underuseEnabled, DateTimeOffset now)
    {
        if (window is null || window.IsSyntheticPlaceholder)
        {
            return;
        }

        var resetKey = ResetKey(window);
        var key = new WindowKey(provider, slot, resetKey);
        if (PaceCalculator.Compute(window, now) is not { } pace)
        {
            _ = this.previousBand.Remove(key);
            return;
        }

        var hadPrevious = this.previousBand.TryGetValue(key, out var previous);
        this.previousBand[key] = pace.State;

        if (!hadPrevious || previous == pace.State)
        {
            return;
        }

        if (pace.State == PaceState.AtRisk)
        {
            var atRiskKey = $"{provider.ConfigId()}|{slot}|atrisk|{resetKey}";
            if (this.MarkFired(atRiskKey))
            {
                this.tray.ShowBalloon(
                    "Pace warning",
                    $"{this.ProviderName(provider)} {slot}: on pace to run out before it resets (~{pace.ProjectedPercent:0}%) - {ResetText(window)}.");
            }
        }
        else if (pace.State == PaceState.Underusing && underuseEnabled)
        {
            var underuseKey = $"{provider.ConfigId()}|{slot}|underuse|{resetKey}";
            if (this.MarkFired(underuseKey))
            {
                this.tray.ShowBalloon(
                    "Pace notice",
                    $"{this.ProviderName(provider)} {slot}: under-using - lots of quota left with time to spare.");
            }
        }
    }

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
