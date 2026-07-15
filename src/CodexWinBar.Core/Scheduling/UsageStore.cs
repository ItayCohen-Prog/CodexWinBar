using System.Net.Http;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Status;

namespace CodexWinBar.Core.Scheduling;

/// <summary>
/// Usage engine that owns refresh cadence, per-provider coalescing, reset-boundary refreshes, and status polling.
/// </summary>
public sealed class UsageStore : IUsageStore
{
    private static readonly TimeSpan FlyoutRefreshDelay = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan ResetBoundaryGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetBoundaryMinimumDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] StartupRetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];
    private const int MaxRememberedResetBoundaries = 64;

    private readonly object sync = new();
    private readonly IReadOnlyList<ProviderDescriptor> descriptors;
    private readonly Dictionary<ProviderId, ProviderDescriptor> descriptorsById;
    private readonly ConfigStore configStore;
    private readonly UiSettingsStore uiStore;
    private readonly Action<string> log;
    private readonly StatusPoller statusPoller;
    private readonly Dictionary<ProviderId, ProviderState> states = [];
    private readonly Dictionary<ProviderId, ProviderRefreshSlot> slots = [];
    private readonly Queue<ResetBoundaryKey> resetBoundaryOrder = new();
    private readonly HashSet<ResetBoundaryKey> attemptedResetBoundaries = [];

    private Timer? periodicTimer;
    private Timer? resetBoundaryTimer;
    private Timer? flyoutTimer;
    private Timer? startupRetryTimer;
    private bool disposed;
    private bool isRefreshing;
    private bool firstBatchCompleted;
    private int startupRetryAttempt;

    /// <summary>
    /// Initializes a new usage store.
    /// </summary>
    /// <param name="descriptors">Provider descriptors in display order.</param>
    /// <param name="configStore">Config store used for provider enablement and source settings.</param>
    /// <param name="uiStore">UI settings store used for cadence and status toggles.</param>
    /// <param name="log">Non-secret diagnostic log sink.</param>
    public UsageStore(
        IReadOnlyList<ProviderDescriptor> descriptors,
        ConfigStore configStore,
        UiSettingsStore uiStore,
        Action<string> log)
    {
        this.descriptors = descriptors;
        this.descriptorsById = descriptors.ToDictionary(descriptor => descriptor.Id);
        this.configStore = configStore;
        this.uiStore = uiStore;
        this.log = log;
        this.statusPoller = new StatusPoller(log);
    }

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public IReadOnlyList<ProviderState> States
    {
        get
        {
            lock (this.sync)
            {
                return this.EnabledDescriptorsNoLock()
                    .Select(descriptor => this.states.TryGetValue(descriptor.Id, out var state)
                        ? state
                        : new ProviderState { Provider = descriptor.Id })
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        this.ThrowIfDisposed();
        this.ReschedulePeriodicTimer();
        _ = this.RefreshAllAsync();
    }

    /// <inheritdoc />
    public void ReloadSchedule()
    {
        this.ThrowIfDisposed();
        this.ReschedulePeriodicTimer();
    }

    /// <inheritdoc />
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        IReadOnlyList<ProviderDescriptor> enabled;
        lock (this.sync)
        {
            if (this.isRefreshing)
            {
                return;
            }

            this.isRefreshing = true;
            enabled = this.EnabledDescriptorsNoLock().ToArray();
        }

        var batchResults = new List<ProviderBatchResult>();
        try
        {
            // Fan out per-provider refreshes: each provider owns an independent slot/generation, so
            // they fetch concurrently instead of head-of-line blocking behind the slowest provider.
            // RefreshProviderCoreAsync never throws for cancellation/errors (it publishes them as a
            // ProviderBatchResult), so WhenAll completes with one result per provider.
            var refreshTasks = enabled.Select(descriptor => this.RefreshProviderCoreAsync(descriptor, ct)).ToArray();
            batchResults.AddRange(await Task.WhenAll(refreshTasks).ConfigureAwait(false));

            await this.PollStatusesAsync(enabled, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (this.sync)
            {
                this.isRefreshing = false;
            }
        }

        this.ScheduleResetBoundaryRefresh();
        this.HandleStartupRetry(batchResults);
    }

    /// <inheritdoc />
    public Task RefreshProviderAsync(ProviderId id, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        if (!this.descriptorsById.TryGetValue(id, out var descriptor))
        {
            return Task.CompletedTask;
        }

        return this.RefreshProviderCoreAsync(descriptor, ct);
    }

    /// <inheritdoc />
    public void NotifyFlyoutOpened()
    {
        this.ThrowIfDisposed();
        this.flyoutTimer?.Dispose();
        this.flyoutTimer = new Timer(
            _ => _ = this.RefreshFlyoutProvidersAsync(),
            null,
            FlyoutRefreshDelay,
            Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            foreach (var slot in this.slots.Values)
            {
                slot.Cancellation?.Cancel();
                slot.Cancellation?.Dispose();
            }
        }

        this.periodicTimer?.Dispose();
        this.resetBoundaryTimer?.Dispose();
        this.flyoutTimer?.Dispose();
        this.startupRetryTimer?.Dispose();
    }

    private async Task<ProviderBatchResult> RefreshProviderCoreAsync(ProviderDescriptor descriptor, CancellationToken ct)
    {
        Task<ProviderBatchResult> inFlight;
        lock (this.sync)
        {
            var slot = this.GetSlotNoLock(descriptor.Id);
            var generation = ++slot.Generation;
            slot.Cancellation?.Cancel();
            slot.Cancellation?.Dispose();
            slot.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Capture the task locally while still under the lock: a concurrent refresh can
            // replace slot.InFlight before this caller awaits, which would await the wrong task.
            inFlight = slot.InFlight = this.FetchAndPublishAsync(descriptor, generation, slot.Cancellation.Token);
            this.SetStateNoLock(descriptor.Id, this.GetStateNoLock(descriptor.Id) with { IsRefreshing = true });
        }

        this.RaiseStateChanged();
        return await inFlight.ConfigureAwait(false);
    }

    private async Task<ProviderBatchResult> FetchAndPublishAsync(
        ProviderDescriptor descriptor,
        int generation,
        CancellationToken ct)
    {
        var config = this.configStore.Load();
        var providerConfig = this.configStore.EntryFor(config, descriptor.Id);
        var context = new FetchContext
        {
            ProviderConfig = providerConfig,
            Http = ProviderHttpClient.Shared,
            Environment = Environment.GetEnvironmentVariable,
            UserProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Now = () => DateTimeOffset.UtcNow,
            Log = this.log,
        };

        try
        {
            var outcome = await Task.Run(() => FetchPipeline.RunAsync(descriptor, context, ct), ct).ConfigureAwait(false);
            if (outcome.Snapshot is not null)
            {
                this.PublishSuccess(descriptor.Id, generation, outcome.Snapshot);
                return ProviderBatchResult.Success(descriptor.Id);
            }

            var error = outcome.Error ?? new InvalidOperationException("Provider fetch failed without an error.");
            this.PublishError(descriptor.Id, generation, error);
            return ProviderBatchResult.Failure(descriptor.Id, error);
        }
        catch (OperationCanceledException ex)
        {
            this.PublishCancellation(descriptor.Id, generation);
            return ProviderBatchResult.Failure(descriptor.Id, ex);
        }
        catch (Exception ex)
        {
            this.PublishError(descriptor.Id, generation, ex);
            return ProviderBatchResult.Failure(descriptor.Id, ex);
        }
    }

    private void PublishSuccess(ProviderId id, int generation, UsageSnapshot snapshot)
    {
        var changed = false;
        lock (this.sync)
        {
            var slot = this.GetSlotNoLock(id);
            if (slot.Generation != generation)
            {
                return;
            }

            slot.PublishedGeneration = generation;
            this.SetStateNoLock(id, this.GetStateNoLock(id) with
            {
                Snapshot = snapshot,
                Error = null,
                IsRefreshing = false,
                NeedsAuthentication = false,
            });
            changed = true;
        }

        if (changed)
        {
            this.RaiseStateChanged();
        }
    }

    private void PublishError(ProviderId id, int generation, Exception error)
    {
        var changed = false;
        lock (this.sync)
        {
            var slot = this.GetSlotNoLock(id);
            if (slot.Generation != generation)
            {
                return;
            }

            this.SetStateNoLock(id, this.GetStateNoLock(id) with
            {
                Error = ErrorMessage(error),
                IsRefreshing = false,
                NeedsAuthentication = error is UnauthorizedProviderException,
            });
            changed = true;
        }

        if (changed)
        {
            // Without this line a provider silently graying out ("signed out") leaves no trace in
            // app.log, making auth regressions undiagnosable after the fact.
            this.log($"fetch failed for {id}: {error.GetType().Name}: {error.Message}"
                + (error is UnauthorizedProviderException ? " -> needs sign-in" : string.Empty));
            this.RaiseStateChanged();
        }
    }

    private void PublishCancellation(ProviderId id, int generation)
    {
        var changed = false;
        lock (this.sync)
        {
            var slot = this.GetSlotNoLock(id);
            if (slot.Generation == generation && slot.PublishedGeneration < generation)
            {
                this.SetStateNoLock(id, this.GetStateNoLock(id) with
                {
                    Error = "cancelled",
                    IsRefreshing = false,
                });
                changed = true;
            }
        }

        if (changed)
        {
            this.RaiseStateChanged();
        }
    }

    private async Task PollStatusesAsync(IReadOnlyList<ProviderDescriptor> enabled, CancellationToken ct)
    {
        if (!this.uiStore.Load().StatusChecksEnabled)
        {
            return;
        }

        foreach (var descriptor in enabled)
        {
            var current = this.GetState(descriptor.Id).ServiceStatus;
            var status = await this.statusPoller.PollAsync(descriptor, current, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            if (status is null || status == current)
            {
                continue;
            }

            lock (this.sync)
            {
                this.SetStateNoLock(descriptor.Id, this.GetStateNoLock(descriptor.Id) with { ServiceStatus = status });
            }

            this.RaiseStateChanged();
        }
    }

    private async Task RefreshFlyoutProvidersAsync()
    {
        var providers = this.StaleOrMissingDescriptors();
        await Task.WhenAll(providers.Select(descriptor =>
            this.RefreshProviderCoreAsync(descriptor, CancellationToken.None))).ConfigureAwait(false);
    }

    private void ReschedulePeriodicTimer()
    {
        this.periodicTimer?.Dispose();
        var cadence = this.uiStore.Load().RefreshCadenceMinutes;
        if (cadence is null)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(cadence.Value);
        this.periodicTimer = new Timer(
            _ => _ = this.RefreshAllAsync(),
            null,
            interval,
            interval);
    }

    private void ScheduleResetBoundaryRefresh()
    {
        this.resetBoundaryTimer?.Dispose();
        var cadence = this.uiStore.Load().RefreshCadenceMinutes;
        if (cadence is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var nextNormalTick = now.AddMinutes(cadence.Value);
        var candidate = this.FindNextResetBoundary(now, nextNormalTick);
        if (candidate is null)
        {
            return;
        }

        this.RememberResetBoundary(candidate.Value.Key);
        var due = candidate.Value.RefreshAt < now.Add(ResetBoundaryMinimumDelay)
            ? ResetBoundaryMinimumDelay
            : candidate.Value.RefreshAt - now;
        this.resetBoundaryTimer = new Timer(
            _ => _ = this.RefreshProviderAsync(candidate.Value.Key.Provider),
            null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    private ResetBoundaryCandidate? FindNextResetBoundary(DateTimeOffset now, DateTimeOffset nextNormalTick)
    {
        ResetBoundaryCandidate? best = null;
        foreach (var state in this.States)
        {
            foreach (var boundary in EnumerateResetBoundaries(state))
            {
                var resetAt = boundary.ResetAt.ToUniversalTime();
                var refreshAt = resetAt.Add(ResetBoundaryGrace);
                if (refreshAt > nextNormalTick)
                {
                    continue;
                }

                var key = new ResetBoundaryKey(state.Provider, boundary.Slot, resetAt.ToUnixTimeSeconds());
                if (this.attemptedResetBoundaries.Contains(key))
                {
                    continue;
                }

                var candidate = new ResetBoundaryCandidate(key, refreshAt);
                if (best is null || candidate.RefreshAt < best.Value.RefreshAt)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private void RememberResetBoundary(ResetBoundaryKey key)
    {
        if (!this.attemptedResetBoundaries.Add(key))
        {
            return;
        }

        this.resetBoundaryOrder.Enqueue(key);
        while (this.resetBoundaryOrder.Count > MaxRememberedResetBoundaries)
        {
            _ = this.attemptedResetBoundaries.Remove(this.resetBoundaryOrder.Dequeue());
        }
    }

    private void HandleStartupRetry(IReadOnlyList<ProviderBatchResult> results)
    {
        if (this.firstBatchCompleted)
        {
            return;
        }

        if (results.Count > 0 && results.All(result => result.Error is null))
        {
            this.firstBatchCompleted = true;
            return;
        }

        if (results.Count == 0 || results.All(result => result.Error is not null && IsNetworkClassError(result.Error)))
        {
            if (this.startupRetryAttempt < StartupRetryDelays.Length)
            {
                var delay = StartupRetryDelays[this.startupRetryAttempt++];
                this.startupRetryTimer?.Dispose();
                this.startupRetryTimer = new Timer(_ => _ = this.RefreshAllAsync(), null, delay, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        this.firstBatchCompleted = true;
    }

    private IReadOnlyList<ProviderDescriptor> StaleOrMissingDescriptors()
    {
        lock (this.sync)
        {
            return this.EnabledDescriptorsNoLock()
                .Where(descriptor =>
                {
                    var state = this.GetStateNoLock(descriptor.Id);
                    return state.Snapshot is null || state.IsStale;
                })
                .ToArray();
        }
    }

    private IReadOnlyList<ProviderDescriptor> EnabledDescriptorsNoLock()
    {
        var config = this.configStore.Load();
        return this.descriptors
            .Where(descriptor =>
            {
                var entry = this.configStore.EntryFor(config, descriptor.Id);
                return entry.Enabled ?? descriptor.Metadata.DefaultEnabled;
            })
            .ToArray();
    }

    private ProviderState GetState(ProviderId id)
    {
        lock (this.sync)
        {
            return this.GetStateNoLock(id);
        }
    }

    private ProviderState GetStateNoLock(ProviderId id)
    {
        return this.states.TryGetValue(id, out var state) ? state : new ProviderState { Provider = id };
    }

    private void SetStateNoLock(ProviderId id, ProviderState state)
    {
        this.states[id] = state;
    }

    private ProviderRefreshSlot GetSlotNoLock(ProviderId id)
    {
        if (!this.slots.TryGetValue(id, out var slot))
        {
            slot = new ProviderRefreshSlot();
            this.slots[id] = slot;
        }

        return slot;
    }

    private void RaiseStateChanged()
    {
        this.StateChanged?.Invoke();
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(UsageStore));
        }
    }

    private static IEnumerable<ResetBoundary> EnumerateResetBoundaries(ProviderState state)
    {
        if (state.Snapshot?.Primary?.ResetsAt is { } primary)
        {
            yield return new ResetBoundary("primary", primary);
        }

        if (state.Snapshot?.Secondary?.ResetsAt is { } secondary)
        {
            yield return new ResetBoundary("secondary", secondary);
        }

        if (state.Snapshot?.Tertiary?.ResetsAt is { } tertiary)
        {
            yield return new ResetBoundary("tertiary", tertiary);
        }

        if (state.Snapshot?.ExtraWindows is not null)
        {
            foreach (var extra in state.Snapshot.ExtraWindows)
            {
                if (extra.Window.ResetsAt is { } resetsAt)
                {
                    yield return new ResetBoundary($"extra:{extra.Id}", resetsAt);
                }
            }
        }
    }

    private static bool IsNetworkClassError(Exception error)
    {
        return error is HttpRequestException or TaskCanceledException or TimeoutException
            || error.InnerException is not null && IsNetworkClassError(error.InnerException);
    }

    private static string ErrorMessage(Exception error)
    {
        return string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
    }

    private sealed class ProviderRefreshSlot
    {
        public int Generation { get; set; }
        public Task<ProviderBatchResult>? InFlight { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public int PublishedGeneration { get; set; }
    }

    private readonly record struct ProviderBatchResult(ProviderId Provider, Exception? Error)
    {
        public static ProviderBatchResult Success(ProviderId provider) => new(provider, null);

        public static ProviderBatchResult Failure(ProviderId provider, Exception error) => new(provider, error);
    }

    private readonly record struct ResetBoundary(string Slot, DateTimeOffset ResetAt);

    private readonly record struct ResetBoundaryKey(ProviderId Provider, string Slot, long UnixSeconds);

    private readonly record struct ResetBoundaryCandidate(ResetBoundaryKey Key, DateTimeOffset RefreshAt);
}
