using System.Net;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class ProviderStrategyHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Zai_strategy_sends_through_fetch_context_http()
    {
        using var handler = new RecordingHandler(_ => Json("""{"data":{"limits":[{"type":"TOKENS","usedPercent":25}]}}"""));
        using var http = new HttpClient(handler);
        var context = CreateContext(http, "zai", apiKey: "test-key");

        var snapshot = await CodexWinBar.Providers.Zai.ZaiProvider.Create().Strategies.Single()
            .FetchAsync(context, CancellationToken.None);

        Assert.Equal(25, snapshot.Primary?.UsedPercent);
        Assert.Equal(["https://api.z.ai/api/monitor/usage/quota/limit"], handler.RequestUris);
    }

    [Fact]
    public async Task OpenAIAdmin_strategy_sends_through_fetch_context_http()
    {
        using var handler = new RecordingHandler(_ => Json(
            """{"data":[{"start_time":1783209600,"results":[{"amount":{"value":12.5}}]}]}"""));
        using var http = new HttpClient(handler);
        var context = CreateContext(http, "openai-admin", apiKey: "sk-admin-test");

        var snapshot = await CodexWinBar.Providers.OpenAIAdmin.OpenAIAdminProvider.Create().Strategies.Single()
            .FetchAsync(context, CancellationToken.None);

        Assert.Equal(12.5, snapshot.Credits?.Remaining);
        var history = Assert.Single(snapshot.HistoricalUsage);
        Assert.Equal("api-spend", history.SeriesId);
        Assert.Equal(12.5, history.Value);
        Assert.Equal("USD", history.Unit);
        Assert.Single(handler.RequestUris);
        Assert.StartsWith("https://api.openai.com/v1/organization/costs?", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouter_strategy_sends_both_requests_through_fetch_context_http()
    {
        using var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/credits" => Json("""{"data":{"total_credits":25.5,"total_usage":5.25}}"""),
            "/api/v1/key" => Json("""{"data":{"limit":100,"usage":12.5}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        var context = CreateContext(http, "openrouter", apiKey: "test-key");

        var snapshot = await CodexWinBar.Providers.OpenRouter.OpenRouterProvider.Create().Strategies.Single()
            .FetchAsync(context, CancellationToken.None);

        Assert.Equal(20.25, snapshot.Credits?.Remaining);
        Assert.Equal(
            ["https://openrouter.ai/api/v1/credits", "https://openrouter.ai/api/v1/key"],
            handler.RequestUris);
    }

    [Fact]
    public async Task Cursor_strategy_sends_usage_and_identity_requests_through_fetch_context_http()
    {
        using var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/usage-summary" => Json("""{"plan":{"usedPercent":20}}"""),
            "/api/auth/me" => Json("""{"email":"me@example.com"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        var context = CreateContext(http, "cursor", cookieHeader: "WorkosCursorSessionToken=test-token");

        var snapshot = await CodexWinBar.Providers.Cursor.CursorProvider.Create().Strategies.Single()
            .FetchAsync(context, CancellationToken.None);

        Assert.Equal(20, snapshot.Primary?.UsedPercent);
        Assert.Equal("me@example.com", snapshot.Identity?.AccountEmail);
        Assert.Equal(
            ["https://cursor.com/api/usage-summary", "https://cursor.com/api/auth/me"],
            handler.RequestUris);
    }

    private static FetchContext CreateContext(
        HttpClient http,
        string providerId,
        string? apiKey = null,
        string? cookieHeader = null) =>
        new()
        {
            ProviderConfig = new ProviderConfigEntry
            {
                Id = providerId,
                ApiKey = apiKey,
                CookieHeader = cookieHeader,
            },
            Http = http,
            Environment = _ => null,
            UserProfileDirectory = Path.GetTempPath(),
            Now = () => Now,
            Log = _ => { },
        };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.RequestUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            return Task.FromResult(respond(request));
        }
    }
}
