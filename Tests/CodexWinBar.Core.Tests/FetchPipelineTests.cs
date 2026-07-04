using CodexWinBar.Core.Config;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class FetchPipelineTests
{
    [Fact]
    public async Task RunAsync_skips_unavailable_records_attempt_and_uses_first_success()
    {
        var unavailable = new StubStrategy("missing", FetchKind.Cli, available: false);
        var success = new StubStrategy("api", FetchKind.ApiToken, available: true, snapshot: Snapshot());
        var outcome = await FetchPipeline.RunAsync(Descriptor(unavailable, success), Context(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Same(success.Snapshot, outcome.Snapshot);
        Assert.Equal(["missing", "api"], outcome.Attempts.Select(a => a.StrategyId).ToArray());
        Assert.False(outcome.Attempts[0].WasAvailable);
        Assert.True(outcome.Attempts[1].WasAvailable);
        Assert.Equal(0, unavailable.FetchCount);
        Assert.Equal(1, success.FetchCount);
    }

    [Fact]
    public async Task RunAsync_continues_only_when_ShouldFallback_allows_it()
    {
        var first = new StubStrategy("oauth", FetchKind.Oauth, available: true, error: new InvalidOperationException("temporary"), shouldFallback: true);
        var second = new StubStrategy("api", FetchKind.ApiToken, available: true, snapshot: Snapshot());
        var outcome = await FetchPipeline.RunAsync(Descriptor(first, second), Context(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["oauth", "api"], outcome.Attempts.Select(a => a.StrategyId).ToArray());
        Assert.Contains("temporary", outcome.Attempts[0].Error);
    }

    [Fact]
    public async Task RunAsync_stops_when_ShouldFallback_denies_fallback()
    {
        var error = new InvalidOperationException("fatal");
        var first = new StubStrategy("oauth", FetchKind.Oauth, available: true, error: error, shouldFallback: false);
        var second = new StubStrategy("api", FetchKind.ApiToken, available: true, snapshot: Snapshot());
        var outcome = await FetchPipeline.RunAsync(Descriptor(first, second), Context(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Same(error, outcome.Error);
        Assert.Equal(["oauth"], outcome.Attempts.Select(a => a.StrategyId).ToArray());
        Assert.Equal(0, second.FetchCount);
    }

    [Fact]
    public async Task RunAsync_returns_NoAvailableStrategy_when_nothing_runs()
    {
        var outcome = await FetchPipeline.RunAsync(
            Descriptor(new StubStrategy("cli", FetchKind.Cli, available: false)),
            Context(),
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.IsType<NoAvailableStrategyException>(outcome.Error);
        Assert.Single(outcome.Attempts);
        Assert.False(outcome.Attempts[0].WasAvailable);
    }

    [Fact]
    public async Task RunAsync_honors_explicit_source_strategy_selection()
    {
        var oauth = new StubStrategy("oauth", FetchKind.Oauth, available: true, snapshot: Snapshot());
        var api = new StubStrategy("api", FetchKind.ApiToken, available: true, snapshot: Snapshot());
        var ctx = Context(new ProviderConfigEntry { Id = "codex", Source = "api" });

        var outcome = await FetchPipeline.RunAsync(Descriptor(oauth, api), ctx, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["api"], outcome.Attempts.Select(a => a.StrategyId).ToArray());
        Assert.Equal(0, oauth.FetchCount);
        Assert.Equal(1, api.FetchCount);
    }

    private static ProviderDescriptor Descriptor(params IFetchStrategy[] strategies) => new()
    {
        Id = ProviderId.Codex,
        Metadata = new ProviderMetadata { DisplayName = "Codex" },
        Branding = new ProviderBranding { GlyphKey = "codex", R = 1, G = 2, B = 3 },
        Strategies = strategies,
    };

    private static FetchContext Context(ProviderConfigEntry? config = null) => new()
    {
        ProviderConfig = config ?? new ProviderConfigEntry { Id = "codex", Source = "auto" },
        Http = new HttpClient(),
        Environment = _ => null,
        UserProfileDirectory = Path.GetTempPath(),
        Now = () => DateTimeOffset.UnixEpoch,
        Log = _ => { },
    };

    private static UsageSnapshot Snapshot() => new()
    {
        Provider = ProviderId.Codex,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Confidence = DataConfidence.Exact,
    };

    private sealed class StubStrategy(
        string id,
        FetchKind kind,
        bool available,
        UsageSnapshot? snapshot = null,
        Exception? error = null,
        bool shouldFallback = false) : IFetchStrategy
    {
        private readonly UsageSnapshot? snapshotValue = snapshot;

        public UsageSnapshot? Snapshot => this.snapshotValue;
        public int FetchCount { get; private set; }
        public string Id { get; } = id;
        public FetchKind Kind { get; } = kind;
        public bool IsAvailable(FetchContext ctx) => available;

        public Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
        {
            this.FetchCount++;
            if (error is not null)
            {
                throw error;
            }

            return Task.FromResult(this.snapshotValue ?? FetchPipelineTests.Snapshot());
        }

        public bool ShouldFallback(Exception error, FetchContext ctx) => shouldFallback;
    }
}
