using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.OpenAIAdmin;

/// <summary>OpenAI Admin provider descriptor factory.</summary>
public static class OpenAIAdminProvider
{
    /// <summary>Creates the OpenAI Admin provider descriptor.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.OpenAIAdmin,
        Metadata = new ProviderMetadata
        {
            DisplayName = "OpenAI Admin",
            DefaultEnabled = false,
            SupportsCredits = true,
            DashboardUrl = new Uri("https://platform.openai.com/usage"),
            StatusPageUrl = new Uri("https://status.openai.com"),
        },
        Branding = new ProviderBranding { GlyphKey = "openaiadmin", R = 16, G = 163, B = 127 },
        Strategies = [new OpenAIAdminApiStrategy()],
    };
}

internal sealed class OpenAIAdminApiStrategy : IFetchStrategy
{
    private static readonly Uri CostsBaseUri = new("https://api.openai.com/v1/organization/costs");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Id => "api";

    public FetchKind Kind => FetchKind.ApiToken;

    public bool IsAvailable(FetchContext ctx) => !string.IsNullOrWhiteSpace(ResolveApiKey(ctx));

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var apiKey = ResolveApiKey(ctx) ?? throw new InvalidOperationException("OpenAI Admin API key is not configured.");
        var now = ctx.Now().ToUniversalTime();
        var start = now.AddDays(-30).ToUnixTimeSeconds();
        var uri = new Uri($"{CostsBaseUri}?start_time={start.ToString(CultureInfo.InvariantCulture)}&bucket_width=1d&limit=31");

        using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("OpenAI Admin API key was rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI Admin costs request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return OpenAIAdminParser.ParseCosts(body, now);
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static string? ResolveApiKey(FetchContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ProviderConfig.ApiKey))
        {
            return ctx.ProviderConfig.ApiKey;
        }

        var adminKey = ctx.Environment("OPENAI_ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            return adminKey;
        }

        var apiKey = ctx.Environment("OPENAI_API_KEY");
        return apiKey?.StartsWith("sk-admin", StringComparison.Ordinal) == true ? apiKey : null;
    }
}

internal static class OpenAIAdminParser
{
    public static UsageSnapshot ParseCosts(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var spend30Days = 0d;
        var todaySpend = 0d;
        var today = now.UtcDateTime.Date;

        foreach (var bucket in ReadBuckets(document.RootElement))
        {
            var amount = SumBucketAmount(bucket);
            spend30Days += amount;
            var start = ReadBucketStart(bucket);
            if (start?.UtcDateTime.Date == today)
            {
                todaySpend += amount;
            }
        }

        return new UsageSnapshot
        {
            Provider = ProviderId.OpenAIAdmin,
            Credits = new CreditsSnapshot
            {
                Remaining = spend30Days,
                Limit = null,
                Unit = "USD (30d)",
                UpdatedAt = now,
            },
            ExtraWindows =
            [
                new NamedRateWindow
                {
                    Id = "today",
                    Title = "Today",
                    UsageKnown = false,
                    Window = new RateWindow
                    {
                        UsedPercent = 0,
                        ResetDescription = string.Create(CultureInfo.InvariantCulture, $"${todaySpend:0.00} today"),
                    },
                },
            ],
            Identity = new ProviderIdentity { LoginMethod = "Admin API key" },
            UpdatedAt = now,
            Confidence = DataConfidence.Estimated,
        };
    }

    private static IEnumerable<JsonElement> ReadBuckets(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : root;
        if (data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var bucket in data.EnumerateArray())
        {
            yield return bucket;
        }
    }

    private static double SumBucketAmount(JsonElement bucket)
    {
        var total = 0d;
        if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                total += ReadAmount(result);
            }
        }
        else
        {
            total += ReadAmount(bucket);
        }

        return total;
    }

    private static double ReadAmount(JsonElement element)
    {
        if (element.TryGetProperty("amount", out var amount))
        {
            if (amount.ValueKind == JsonValueKind.Object)
            {
                return ReadFirstNumber(amount, "value", "amount") ?? 0;
            }

            return ReadNumberElement(amount) ?? 0;
        }

        return ReadFirstNumber(element, "cost", "total") ?? 0;
    }

    private static DateTimeOffset? ReadBucketStart(JsonElement bucket)
    {
        var epoch = ReadFirstNumber(bucket, "start_time", "startTime");
        return epoch is null ? null : DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).ToUniversalTime();
    }

    private static double? ReadFirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property))
            {
                var value = ReadNumberElement(property);
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static double? ReadNumberElement(JsonElement property)
    {
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }
}
