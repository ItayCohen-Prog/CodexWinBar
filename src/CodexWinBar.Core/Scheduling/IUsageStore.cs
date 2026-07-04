using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Core.Scheduling;

/// <summary>Immutable view of everything the UI renders for one provider.</summary>
public sealed record ProviderState
{
    public required ProviderId Provider { get; init; }
    public UsageSnapshot? Snapshot { get; init; }
    /// <summary>Human-readable message of the last failed fetch; null when the last fetch succeeded.</summary>
    public string? Error { get; init; }
    /// <summary>True while a fetch for this provider is in flight.</summary>
    public bool IsRefreshing { get; init; }
    /// <summary>Last fetch errored (upstream staleness semantics — no TTL).</summary>
    public bool IsStale => this.Error is not null;
    public ProviderStatus? ServiceStatus { get; init; }
    /// <summary>Re-auth needed (401/403 class failures).</summary>
    public bool NeedsAuthentication { get; init; }
}

/// <summary>
/// The engine facade the UI consumes — owns the refresh scheduler, per-provider coalescing, reset-boundary
/// refresh, and status polling. Implemented in wave WA2; UI waves depend only on this interface.
/// </summary>
public interface IUsageStore : IDisposable
{
    /// <summary>Current state of every enabled provider, in display order.</summary>
    IReadOnlyList<ProviderState> States { get; }

    /// <summary>Raised on any state change. May fire on any thread; subscribers marshal themselves.</summary>
    event Action? StateChanged;

    /// <summary>Starts the periodic scheduler and performs an initial refresh.</summary>
    void Start();

    /// <summary>Refreshes all enabled providers now (respects the single-batch gate).</summary>
    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Refreshes one provider now with replace-semantics coalescing (amendment A8).</summary>
    Task RefreshProviderAsync(ProviderId id, CancellationToken ct = default);

    /// <summary>Flyout-open hook: refresh stale/never-fetched providers after the upstream 1.2s delay.</summary>
    void NotifyFlyoutOpened();

    /// <summary>Re-arms the periodic refresh timer from current UiSettings (call after a cadence change).</summary>
    void ReloadSchedule();
}
