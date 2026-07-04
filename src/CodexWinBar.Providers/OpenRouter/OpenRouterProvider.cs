using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.OpenRouter;

/// <summary>OpenRouter provider descriptor factory.</summary>
public static class OpenRouterProvider
{
    /// <summary>Creates the OpenRouter provider descriptor.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.OpenRouter,
        Metadata = new ProviderMetadata
        {
            DisplayName = "OpenRouter",
            DefaultEnabled = false,
            SupportsCredits = true,
            DashboardUrl = new Uri("https://openrouter.ai/credits"),
            StatusPageUrl = new Uri("https://status.openrouter.ai"),
        },
        Branding = new ProviderBranding { GlyphKey = "openrouter", R = 101, G = 82, B = 255 },
        Strategies = [new OpenRouterApiStrategy()],
    };
}

internal sealed class OpenRouterApiStrategy : IFetchStrategy
{
    private static readonly Uri CreditsUri = new("https://openrouter.ai/api/v1/credits");
    private static readonly Uri KeyUri = new("https://openrouter.ai/api/v1/key");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Id => "api";

    public FetchKind Kind => FetchKind.ApiToken;

    public bool IsAvailable(FetchContext ctx) => !string.IsNullOrWhiteSpace(ResolveApiKey(ctx));

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var apiKey = ResolveApiKey(ctx) ?? throw new InvalidOperationException("OpenRouter API key is not configured.");
        using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
        var creditsJson = await GetJsonAsync(CreditsUri, apiKey, timeout.Token).ConfigureAwait(false);
        var keyJson = await GetJsonAsync(KeyUri, apiKey, timeout.Token).ConfigureAwait(false);
        return OpenRouterParser.Parse(creditsJson, keyJson, ctx.Now().ToUniversalTime());
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static string? ResolveApiKey(FetchContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ProviderConfig.ApiKey))
        {
            return ctx.ProviderConfig.ApiKey;
        }

        return ctx.Environment("OPENROUTER_API_KEY");
    }

    private static async Task<string> GetJsonAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await ProviderHttpClient.Shared.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("OpenRouter API key was rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenRouter request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return body;
    }
}

internal static class OpenRouterParser
{
    public static UsageSnapshot Parse(string creditsJson, string keyJson, DateTimeOffset now)
    {
        var (totalCredits, totalUsage) = ParseCredits(creditsJson);
        var key = ParseKey(keyJson);
        var credits = new CreditsSnapshot
        {
            Remaining = totalCredits - totalUsage,
            Limit = totalCredits,
            Unit = "credits",
            UpdatedAt = now,
        };

        return new UsageSnapshot
        {
            Provider = ProviderId.OpenRouter,
            Primary = key.Limit is > 0
                ? new RateWindow
                {
                    UsedPercent = ClampPercent(key.Usage / key.Limit.Value * 100),
                    ResetDescription = key.ResetDescription,
                }
                : null,
            Credits = credits,
            Identity = new ProviderIdentity
            {
                AccountOrganization = string.IsNullOrWhiteSpace(key.Label) ? null : key.Label,
                LoginMethod = "API key",
            },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        };
    }

    private static (double TotalCredits, double TotalUsage) ParseCredits(string json)
    {
        using var document = JsonDocument.Parse(json);
        var data = RequiredProperty(document.RootElement, "data");
        return (ReadNumber(data, "total_credits"), ReadNumber(data, "total_usage"));
    }

    private static KeyPayload ParseKey(string json)
    {
        using var document = JsonDocument.Parse(json);
        var data = RequiredProperty(document.RootElement, "data");
        var limit = ReadNullableNumber(data, "limit");
        var usage = ReadNullableNumber(data, "usage") ?? 0;
        var label = ReadString(data, "label");
        var remaining = ReadNullableNumber(data, "limit_remaining");
        var resetDescription = remaining.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"Limit remaining: {remaining.Value:0.##}")
            : null;
        return new KeyPayload(limit, usage, label, resetDescription);
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property))
        {
            return property;
        }

        throw new InvalidOperationException($"OpenRouter response is missing '{name}'.");
    }

    private static double ReadNumber(JsonElement element, string name) =>
        ReadNullableNumber(element, name) ?? throw new InvalidOperationException($"OpenRouter response is missing '{name}'.");

    private static double? ReadNullableNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => throw new InvalidOperationException($"OpenRouter field '{name}' is not numeric."),
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);

    private sealed record KeyPayload(double? Limit, double Usage, string? Label, string? ResetDescription);
}
