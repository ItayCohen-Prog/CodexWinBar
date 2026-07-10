using CodexWinBar.Core.Auth;
using CodexWinBar.Core.Models;

namespace CodexWinBar.Core.Providers;

/// <summary>How a strategy obtains data — port of upstream ProviderFetchStrategy.Kind.</summary>
public enum FetchKind
{
    Oauth,
    ApiToken,
    LocalProbe,
    Web,
    Cli,
}

/// <summary>Ambient services and per-provider settings handed to strategies.</summary>
public sealed class FetchContext
{
    /// <summary>Config entry for this provider (apiKey, source override, …); never null.</summary>
    public required Config.ProviderConfigEntry ProviderConfig { get; init; }
    /// <summary>Shared HTTP client (15s timeout, common UA). Strategies must not dispose it.</summary>
    public required HttpClient Http { get; init; }
    /// <summary>Environment variable lookup seam (testable).</summary>
    public required Func<string, string?> Environment { get; init; }
    /// <summary>User profile directory seam (testable); default resolves %USERPROFILE%.</summary>
    public required string UserProfileDirectory { get; init; }
    /// <summary>Clock seam (testable).</summary>
    public required Func<DateTimeOffset> Now { get; init; }
    /// <summary>Diagnostics sink; messages must never contain secrets.</summary>
    public required Action<string> Log { get; init; }

    /// <summary>Test seam: overrides <see cref="Credentials"/> when set.</summary>
    public AppCredentialStore? CredentialStoreOverride { get; init; }

    private AppCredentialStore? credentials;

    /// <summary>App-owned credential store holding tokens captured by in-app sign-in.</summary>
    public AppCredentialStore Credentials =>
        this.CredentialStoreOverride ?? (this.credentials ??= new AppCredentialStore(this.Environment, this.Log));
}

/// <summary>One way of fetching usage for a provider. Strategies are stateless and thread-safe.</summary>
public interface IFetchStrategy
{
    /// <summary>Stable id used in diagnostics and config `source` matching, e.g. "oauth", "api".</summary>
    string Id { get; }

    FetchKind Kind { get; }

    /// <summary>Cheap availability check (credential file exists, api key configured…). Must not do I/O beyond stat/read of local files.</summary>
    bool IsAvailable(FetchContext ctx);

    Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct);

    /// <summary>Whether the pipeline may try the next strategy after <paramref name="error"/>.</summary>
    bool ShouldFallback(Exception error, FetchContext ctx);
}
