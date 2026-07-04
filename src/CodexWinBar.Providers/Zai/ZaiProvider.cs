using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Zai;

/// <summary>z.ai provider descriptor factory.</summary>
public static class ZaiProvider
{
    /// <summary>Creates the z.ai provider descriptor.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Zai,
        Metadata = new ProviderMetadata
        {
            DisplayName = "z.ai",
            DefaultEnabled = false,
            DashboardUrl = new Uri("https://z.ai"),
        },
        Branding = new ProviderBranding { GlyphKey = "zai", R = 205, G = 62, B = 253 },
        Strategies = [new ZaiApiStrategy()],
    };
}

internal sealed class ZaiApiStrategy : IFetchStrategy
{
    private static readonly Uri QuotaUri = new("https://api.z.ai/api/monitor/usage/quota/limit");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Id => "api";

    public FetchKind Kind => FetchKind.ApiToken;

    public bool IsAvailable(FetchContext ctx) => !string.IsNullOrWhiteSpace(ResolveApiKey(ctx));

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var apiKey = ResolveApiKey(ctx) ?? throw new InvalidOperationException("z.ai API key is not configured.");
        using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, QuotaUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("z.ai API key was rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"z.ai quota request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return ZaiParser.Parse(body, ctx.Now().ToUniversalTime());
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static string? ResolveApiKey(FetchContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ProviderConfig.ApiKey))
        {
            return ctx.ProviderConfig.ApiKey;
        }

        return ctx.Environment("Z_AI_API_KEY");
    }
}

internal static class ZaiParser
{
    public static UsageSnapshot Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.TryGetProperty("data", out var dataElement)
            ? dataElement
            : document.RootElement;
        var plan = ReadFirstString(data, "planName", "plan", "plan_type", "packageName");
        var windows = ReadLimitWindows(data).ToList();
        var primary = windows.FirstOrDefault(window => IsTokenLimit(window.Id)) ?? windows.FirstOrDefault();
        var secondary = windows.FirstOrDefault(window => !ReferenceEquals(window, primary) && IsTimeLimit(window.Id))
            ?? windows.FirstOrDefault(window => !ReferenceEquals(window, primary));
        var extraWindows = windows
            .Where(window => !ReferenceEquals(window, primary) && !ReferenceEquals(window, secondary))
            .Select(window => new NamedRateWindow
            {
                Id = window.Id,
                Title = window.Title,
                Window = window.Window,
                UsageKnown = true,
            })
            .ToList();

        return new UsageSnapshot
        {
            Provider = ProviderId.Zai,
            Primary = primary?.Window,
            Secondary = secondary?.Window,
            ExtraWindows = extraWindows,
            Identity = new ProviderIdentity
            {
                Plan = string.IsNullOrWhiteSpace(plan) ? null : plan,
                LoginMethod = "API key",
            },
            UpdatedAt = now,
            Confidence = windows.Count > 0 ? DataConfidence.Exact : DataConfidence.Unknown,
        };
    }

    private static IEnumerable<ParsedWindow> ReadLimitWindows(JsonElement data)
    {
        if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                var parsed = TryReadWindow(limit);
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }
        }

        foreach (var counterName in new[] { "usageDetails", "tokenUsage", "promptUsage", "modelUsage" })
        {
            if (!data.TryGetProperty(counterName, out var counters) || counters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var counter in counters.EnumerateArray())
            {
                var used = ReadFirstNumber(counter, "used", "usage", "tokens", "promptTokens", "totalTokens");
                var limit = ReadFirstNumber(counter, "limit", "quota", "total");
                if (used is null || limit is null or <= 0)
                {
                    continue;
                }

                var id = ReadFirstString(counter, "type", "name", "model") ?? $"{counterName}-{index}";
                yield return new ParsedWindow(
                    id,
                    ToTitle(id),
                    new RateWindow { UsedPercent = ClampPercent(used.Value / limit.Value * 100) });
                index++;
            }
        }
    }

    private static ParsedWindow? TryReadWindow(JsonElement limit)
    {
        var type = ReadFirstString(limit, "type", "limitType", "quotaType", "name") ?? "quota";
        var percent = ReadFirstNumber(limit, "usedPercent", "usagePercent", "percent", "percentage");
        var used = ReadFirstNumber(limit, "used", "usage", "usedAmount", "current", "consumed", "value");
        var total = ReadFirstNumber(limit, "limit", "quota", "total", "max", "amount");
        if (percent is null && used is not null && total is > 0)
        {
            percent = used.Value / total.Value * 100;
        }

        if (percent is null)
        {
            return null;
        }

        return new ParsedWindow(
            type,
            ToTitle(type),
            new RateWindow
            {
                UsedPercent = ClampPercent(percent.Value),
                WindowMinutes = ReadWindowMinutes(limit),
                ResetsAt = ReadReset(limit),
            });
    }

    private static int? ReadWindowMinutes(JsonElement element)
    {
        var number = ReadFirstNumber(element, "windowNumber", "cycleNumber", "duration", "period");
        var unit = ReadFirstString(element, "windowUnit", "cycleUnit", "unit", "periodUnit");
        if (number is null || string.IsNullOrWhiteSpace(unit))
        {
            return null;
        }

        return unit.ToLowerInvariant() switch
        {
            "minute" or "minutes" or "min" or "m" => (int)Math.Round(number.Value),
            "hour" or "hours" or "h" => (int)Math.Round(number.Value * 60),
            "day" or "days" or "d" => (int)Math.Round(number.Value * 1440),
            _ => null,
        };
    }

    private static DateTimeOffset? ReadReset(JsonElement element)
    {
        var epoch = ReadFirstNumber(element, "nextResetTime", "resetTime", "resetAt");
        if (epoch is null)
        {
            return null;
        }

        return epoch > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch.Value).ToUniversalTime()
            : DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).ToUniversalTime();
    }

    private static double? ReadFirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
            {
                return value;
            }

            if (property.ValueKind == JsonValueKind.String &&
                double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool IsTokenLimit(string id) => id.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeLimit(string id) => id.Contains("TIME", StringComparison.OrdinalIgnoreCase);

    private static string ToTitle(string id) => id.Replace('_', ' ').Replace('-', ' ');

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);

    private sealed record ParsedWindow(string Id, string Title, RateWindow Window);
}
