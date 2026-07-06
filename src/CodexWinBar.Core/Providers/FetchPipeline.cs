using CodexWinBar.Core.Models;

namespace CodexWinBar.Core.Providers;

/// <summary>Record of one strategy attempt, for diagnostics UI and logs.</summary>
public sealed record FetchAttempt(string StrategyId, FetchKind Kind, bool WasAvailable, string? Error);

/// <summary>Result envelope: the snapshot (on success) plus every attempt made.</summary>
public sealed record FetchOutcome
{
    public UsageSnapshot? Snapshot { get; init; }
    public required IReadOnlyList<FetchAttempt> Attempts { get; init; }
    public Exception? Error { get; init; }
    public bool Succeeded => this.Snapshot is not null;
}

/// <summary>The provider's auth material is missing/expired/revoked; UI shows re-auth guidance.</summary>
public sealed class UnauthorizedProviderException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>No strategy was available for the provider (nothing configured/installed, or an invalid explicit source).</summary>
public sealed class NoAvailableStrategyException(ProviderId provider, string? message = null)
    : Exception(message ?? $"No available data source for {provider}.")
{
    public ProviderId Provider { get; } = provider;
}

/// <summary>Port of upstream ProviderFetchPipeline: ordered strategies, availability skip, gated fallback.</summary>
public static class FetchPipeline
{
    public static async Task<FetchOutcome> RunAsync(
        ProviderDescriptor descriptor, FetchContext ctx, CancellationToken ct)
    {
        var attempts = new List<FetchAttempt>();
        Exception? lastError = null;

        var strategies = ResolveStrategies(descriptor, ctx, out var unmatchedSource);
        if (unmatchedSource is not null)
        {
            return new FetchOutcome
            {
                Attempts = attempts,
                Error = new NoAvailableStrategyException(
                    descriptor.Id,
                    $"No available data source for {descriptor.Id}: configured source '{unmatchedSource}' does not match any strategy."),
            };
        }

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            if (!strategy.IsAvailable(ctx))
            {
                attempts.Add(new(strategy.Id, strategy.Kind, WasAvailable: false, Error: null));
                continue;
            }

            try
            {
                var snapshot = await strategy.FetchAsync(ctx, ct).ConfigureAwait(false);
                attempts.Add(new(strategy.Id, strategy.Kind, WasAvailable: true, Error: null));
                return new FetchOutcome { Snapshot = snapshot, Attempts = attempts };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Only the caller's cancellation is real cancellation; strategy-internal timeouts
                // (linked timeout CTS inside providers) fall through to the generic handler below
                // so the attempt is recorded and fallback is honored.
                throw;
            }
            catch (Exception ex)
            {
                attempts.Add(new(strategy.Id, strategy.Kind, WasAvailable: true, Error: ex.Message));
                lastError = ex;
                if (!strategy.ShouldFallback(ex, ctx))
                {
                    break;
                }
            }
        }

        lastError ??= new NoAvailableStrategyException(descriptor.Id);
        return new FetchOutcome { Attempts = attempts, Error = lastError };
    }

    /// <summary>
    /// Honors an explicit config `source` (single strategy) or descriptor order for "auto".
    /// An explicit source that matches no strategy is reported via <paramref name="unmatchedSource"/>
    /// instead of silently falling back to all strategies.
    /// </summary>
    private static IReadOnlyList<IFetchStrategy> ResolveStrategies(
        ProviderDescriptor descriptor, FetchContext ctx, out string? unmatchedSource)
    {
        unmatchedSource = null;
        var source = ctx.ProviderConfig.Source;
        if (!string.IsNullOrEmpty(source) && !string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var match = descriptor.Strategies.Where(s =>
                string.Equals(s.Id, source, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count > 0)
            {
                return match;
            }

            unmatchedSource = source;
            return [];
        }

        return descriptor.Strategies;
    }
}
