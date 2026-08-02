using System.Reflection;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class UsageStoreTests
{
    [Fact]
    public async Task RefreshProviderAsync_isolates_each_StateChanged_subscriber_and_log_sink()
    {
        using var temp = TempDir.Create();
        var strategy = new StubStrategy(Snapshot());
        var store = CreateStore(temp, strategy, _ => throw new InvalidOperationException("log failed"));
        var successfulSubscriberCalls = 0;
        store.StateChanged += () => throw new InvalidOperationException("subscriber failed");
        store.StateChanged += () => successfulSubscriberCalls++;

        await store.RefreshProviderAsync(ProviderId.Codex);

        var state = Assert.Single(store.States);
        Assert.NotNull(state.Snapshot);
        Assert.False(state.IsRefreshing);
        Assert.True(successfulSubscriberCalls >= 2);
        store.Dispose();
    }

    [Fact]
    public void States_retains_last_good_config_until_last_write_timestamp_changes_again()
    {
        using var temp = TempDir.Create();
        var messages = new List<string>();
        var configStore = CreateConfigStore(temp, messages.Add);
        SaveEnabled(configStore, enabled: true);
        var store = CreateStore(temp, new StubStrategy(Snapshot()), messages.Add, configStore);

        Assert.Single(store.States);

        var path = configStore.ResolvePath();
        File.WriteAllText(path, "{");
        var malformedStamp = DateTime.UtcNow.AddSeconds(10);
        File.SetLastWriteTimeUtc(path, malformedStamp);

        Assert.Single(store.States);
        Assert.Single(store.States);
        Assert.Single(messages, message => message.Contains("keeping last good config", StringComparison.Ordinal));

        SaveEnabled(configStore, enabled: false);
        File.SetLastWriteTimeUtc(path, malformedStamp.AddSeconds(10));

        Assert.Empty(store.States);
        store.Dispose();
    }

    [Fact]
    public async Task Provider_refresh_rearms_same_boundary_until_active_callback_claims_it_then_schedules_next()
    {
        using var temp = TempDir.Create();
        var strategy = new StubStrategy(Snapshot(
            primaryReset: DateTimeOffset.UtcNow.AddMinutes(-1),
            secondaryReset: DateTimeOffset.UtcNow.AddMinutes(2)));
        using var store = CreateStore(temp, strategy, _ => { }, cadenceMinutes: 5);

        await store.RefreshProviderAsync(ProviderId.Codex);
        var first = ReadScheduledBoundary(store);

        await store.RefreshProviderAsync(ProviderId.Codex);
        var replacement = ReadScheduledBoundary(store);

        Assert.Contains("Slot = primary", first.Key.ToString(), StringComparison.Ordinal);
        Assert.Equal(first.Key.ToString(), replacement.Key.ToString());
        Assert.True(replacement.Generation > first.Generation);
        Assert.Equal(2, strategy.FetchCount);

        await InvokeResetBoundaryCallback(store, first.Key, first.Generation);

        Assert.Equal(2, strategy.FetchCount);
        Assert.Equal(replacement.Key.ToString(), ReadScheduledBoundary(store).Key.ToString());

        await InvokeResetBoundaryCallback(store, replacement.Key, replacement.Generation);

        var next = ReadScheduledBoundary(store);
        Assert.Equal(3, strategy.FetchCount);
        Assert.Contains("Slot = secondary", next.Key.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAllAsync_leaves_status_null_without_status_integration()
    {
        using var temp = TempDir.Create();
        using var store = CreateStore(
            temp,
            new StubStrategy(Snapshot()),
            _ => { },
            statusChecksEnabled: true);

        await store.RefreshAllAsync();

        Assert.Null(Assert.Single(store.States).ServiceStatus);
    }

    private static UsageStore CreateStore(
        TempDir temp,
        StubStrategy strategy,
        Action<string> log,
        ConfigStore? configStore = null,
        int? cadenceMinutes = null,
        bool statusChecksEnabled = false)
    {
        configStore ??= CreateConfigStore(temp, log);
        SaveEnabled(configStore, enabled: true);
        var uiStore = new UiSettingsStore(log, Path.Combine(temp.Path, "ui"));
        uiStore.Save(new UiSettings
        {
            SettingsVersion = 1,
            RefreshCadenceMinutes = cadenceMinutes,
            StatusChecksEnabled = statusChecksEnabled,
        });

        return new UsageStore([Descriptor(strategy)], configStore, uiStore, log);
    }

    private static ConfigStore CreateConfigStore(TempDir temp, Action<string> log)
    {
        var path = Path.Combine(temp.Path, "config.json");
        return new ConfigStore(name => name == "CODEXBAR_CONFIG" ? path : null, temp.Path, log);
    }

    private static void SaveEnabled(ConfigStore configStore, bool enabled)
    {
        configStore.Save(new CodexBarConfig
        {
            Providers =
            [
                new ProviderConfigEntry
                {
                    Id = ProviderId.Codex.ConfigId(),
                    Enabled = enabled,
                    Source = "test",
                },
            ],
        });
    }

    private static ProviderDescriptor Descriptor(IFetchStrategy strategy) => new()
    {
        Id = ProviderId.Codex,
        Metadata = new ProviderMetadata { DisplayName = "Codex" },
        Branding = new ProviderBranding { GlyphKey = "codex", R = 1, G = 2, B = 3 },
        Strategies = [strategy],
    };

    private static UsageSnapshot Snapshot(
        DateTimeOffset? primaryReset = null,
        DateTimeOffset? secondaryReset = null) => new()
    {
        Provider = ProviderId.Codex,
        Primary = primaryReset is null
            ? null
            : new RateWindow { UsedPercent = 25, ResetsAt = primaryReset },
        Secondary = secondaryReset is null
            ? null
            : new RateWindow { UsedPercent = 30, ResetsAt = secondaryReset },
        UpdatedAt = DateTimeOffset.UtcNow,
        Confidence = DataConfidence.Exact,
    };

    private static (object Key, long Generation) ReadScheduledBoundary(UsageStore store)
    {
        var keyField = typeof(UsageStore).GetField("scheduledResetBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
        var generationField = typeof(UsageStore).GetField("resetBoundaryTimerGeneration", BindingFlags.Instance | BindingFlags.NonPublic);
        var timerField = typeof(UsageStore).GetField("resetBoundaryTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(timerField?.GetValue(store));
        var key = keyField?.GetValue(store);
        Assert.NotNull(key);
        return (key, Assert.IsType<long>(generationField?.GetValue(store)));
    }

    private static async Task InvokeResetBoundaryCallback(UsageStore store, object key, long generation)
    {
        var method = typeof(UsageStore).GetMethod(
            "HandleResetBoundaryTimerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var task = method?.Invoke(store, [key, generation]);
        await Assert.IsAssignableFrom<Task>(task);
    }

    private sealed class StubStrategy(UsageSnapshot snapshot) : IFetchStrategy
    {
        private int fetchCount;

        public string Id => "test";

        public FetchKind Kind => FetchKind.ApiToken;

        public int FetchCount => Volatile.Read(ref this.fetchCount);

        public bool IsAvailable(FetchContext ctx) => true;

        public Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref this.fetchCount);
            return Task.FromResult(snapshot);
        }

        public bool ShouldFallback(Exception error, FetchContext ctx) => false;
    }
}
