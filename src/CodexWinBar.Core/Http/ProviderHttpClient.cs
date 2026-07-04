using System.Net;

namespace CodexWinBar.Core.Http;

/// <summary>
/// Shared HTTP transport for provider fetch and status requests.
/// </summary>
public static class ProviderHttpClient
{
    /// <summary>
    /// Shared provider HTTP client. The client intentionally has no global timeout because each strategy or
    /// poller operation links its own cancellation token with a per-request timeout.
    /// </summary>
    public static HttpClient Shared { get; } = CreateSharedClient();

    /// <summary>
    /// Creates a cancellation source linked to <paramref name="outer"/> that cancels after <paramref name="timeout"/>.
    /// </summary>
    /// <param name="outer">Outer cancellation token.</param>
    /// <param name="timeout">Per-request timeout.</param>
    /// <returns>A linked cancellation token source.</returns>
    public static CancellationTokenSource TimeoutCts(CancellationToken outer, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexWinBar");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
