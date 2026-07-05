using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Cursor;

/// <summary>Cursor provider descriptor factory.</summary>
public static class CursorProvider
{
    /// <summary>Creates the Cursor provider descriptor used by the provider catalog.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Cursor,
        Metadata = new ProviderMetadata
        {
            DisplayName = "Cursor",
            // Cursor bills on a monthly cycle rather than session/weekly windows, so the primary
            // window is the included plan usage and the secondary is the Auto + Composer pool.
            SessionLabel = "Plan",
            // The secondary pool is Cursor's "Auto + Composer" usage; the single word fits the
            // flyout's fixed-width label column without ellipsis and is the recognizable half.
            WeeklyLabel = "Composer",
            DefaultEnabled = false,
            SupportsCredits = false,
            DashboardUrl = new Uri("https://cursor.com/dashboard"),
            StatusPageUrl = new Uri("https://status.cursor.com"),
        },
        Branding = new ProviderBranding { GlyphKey = "cursor", R = 70, G = 74, B = 82 },
        Strategies = [new CursorWebStrategy()],
    };
}

/// <summary>
/// Fetches Cursor usage using a signed-in browser session cookie (upstream CodexBar reads the same
/// cursor.com endpoints via the WorkosCursorSessionToken cookie).
///
/// NOTE: The cursor.com endpoints below are modeled from upstream steipete/CodexBar's docs/cursor.md;
/// their JSON field names are not published, so <see cref="CursorParser"/> probes a range of candidate
/// keys defensively (same approach as the z.ai strategy). This path has NOT been live-verified against a
/// real Cursor session on Windows yet — the display is currently exercised through the fake-data store.
/// Re-verify field names once a real session cookie is available.
/// </summary>
internal sealed class CursorWebStrategy : IFetchStrategy
{
    private static readonly Uri UsageSummaryUri = new("https://cursor.com/api/usage-summary");
    private static readonly Uri MeUri = new("https://cursor.com/api/auth/me");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Id => "web";

    public FetchKind Kind => FetchKind.Web;

    public bool IsAvailable(FetchContext ctx) => !string.IsNullOrWhiteSpace(ResolveCookieHeader(ctx));

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var cookie = ResolveCookieHeader(ctx) ?? throw new InvalidOperationException("Cursor session cookie is not configured.");
        using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
        var usageJson = await GetJsonAsync(UsageSummaryUri, cookie, timeout.Token).ConfigureAwait(false);

        string? meJson = null;
        try
        {
            meJson = await GetJsonAsync(MeUri, cookie, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not UnauthorizedProviderException)
        {
            ctx.Log($"Cursor identity request unavailable: {ex.GetType().Name}.");
        }

        return CursorParser.Parse(usageJson, meJson, ctx.Now().ToUniversalTime());
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static string? ResolveCookieHeader(FetchContext ctx)
    {
        // A full "Cookie:" header copied from a signed-in browser wins outright.
        if (!string.IsNullOrWhiteSpace(ctx.ProviderConfig.CookieHeader))
        {
            return ctx.ProviderConfig.CookieHeader.Trim();
        }

        if (ctx.Environment("CURSOR_COOKIE") is { Length: > 0 } rawCookie)
        {
            return rawCookie.Trim();
        }

        // Otherwise accept just the session token and wrap it in the cookie Cursor expects.
        var token = ctx.Environment("CURSOR_SESSION_TOKEN");
        return string.IsNullOrWhiteSpace(token) ? null : $"WorkosCursorSessionToken={token.Trim()}";
    }

    private static async Task<string> GetJsonAsync(Uri uri, string cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        using var response = await ProviderHttpClient.Shared.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("Cursor session cookie was rejected or has expired.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Cursor request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return body;
    }
}

internal static class CursorParser
{
    public static UsageSnapshot Parse(string usageJson, string? meJson, DateTimeOffset now)
    {
        using var usage = JsonDocument.Parse(usageJson);
        var root = usage.RootElement;
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : root;

        var reset = ReadBillingCycleEnd(data);
        var plan = ReadPercentWindow(data, reset, "plan", "included", "planUsage", "membership", "quota");
        var autoComposer = ReadPercentWindow(data, reset, "auto", "composer", "autoComposer", "agent");
        var apiModels = ReadPercentWindow(data, reset, "api", "namedModel", "byok", "usageBased");

        var extras = new List<NamedRateWindow>();
        if (apiModels is not null)
        {
            extras.Add(new NamedRateWindow { Id = "cursor-api", Title = "API (models)", Window = apiModels });
        }

        var extraUsd = ReadExtraUsageUsd(data);
        if (extraUsd is not null)
        {
            extras.Add(new NamedRateWindow
            {
                Id = "cursor-extra-usage",
                Title = "Extra usage",
                UsageKnown = false,
                Window = new RateWindow
                {
                    UsedPercent = 0,
                    ResetDescription = string.Create(CultureInfo.InvariantCulture, $"${extraUsd.Value:0.00} this cycle"),
                },
            });
        }

        var identity = ReadIdentity(meJson, ReadString(data, "plan", "planName", "membershipType"));

        var hasData = plan is not null || autoComposer is not null || extras.Count > 0;
        return new UsageSnapshot
        {
            Provider = ProviderId.Cursor,
            Primary = plan,
            Secondary = autoComposer,
            ExtraWindows = extras,
            Identity = identity,
            UpdatedAt = now,
            Confidence = hasData ? DataConfidence.Exact : DataConfidence.Unknown,
        };
    }

    private static RateWindow? ReadPercentWindow(JsonElement data, DateTimeOffset? reset, params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
        {
            if (!data.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var percent = ReadFirstNumber(node, "usedPercent", "percent", "percentage", "utilization");
            if (percent is null)
            {
                var used = ReadFirstNumber(node, "used", "usage", "consumed", "current");
                var limit = ReadFirstNumber(node, "limit", "quota", "total", "included", "max");
                if (used is not null && limit is > 0)
                {
                    percent = used.Value / limit.Value * 100;
                }
            }

            if (percent is not null)
            {
                return new RateWindow
                {
                    UsedPercent = ClampPercent(percent.Value),
                    WindowMinutes = 43200,
                    ResetsAt = reset,
                };
            }
        }

        return null;
    }

    private static double? ReadExtraUsageUsd(JsonElement data)
    {
        foreach (var key in new[] { "onDemand", "extraUsage", "usageBased", "additionalUsage", "overage" })
        {
            if (!data.TryGetProperty(key, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.Object)
            {
                // Cents-named fields are always minor units; dollar-named fields are taken verbatim.
                var cents = ReadFirstNumber(node, "cents", "amountCents");
                if (cents is not null)
                {
                    return cents.Value / 100;
                }

                var dollars = ReadFirstNumber(node, "amount", "usd", "dollars", "cost");
                if (dollars is not null)
                {
                    return dollars;
                }
            }
            else if (ReadNumber(node) is { } bare)
            {
                return bare;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadBillingCycleEnd(JsonElement data)
    {
        foreach (var key in new[] { "billingCycleEnd", "currentPeriodEnd", "periodEnd", "resetsAt", "renewalDate", "cycleEnd" })
        {
            if (!data.TryGetProperty(key, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var epoch))
            {
                return epoch > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).ToUniversalTime()
                    : DateTimeOffset.FromUnixTimeSeconds(epoch).ToUniversalTime();
            }

            if (node.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static ProviderIdentity? ReadIdentity(string? meJson, string? planFromUsage)
    {
        string? email = null;
        if (!string.IsNullOrWhiteSpace(meJson))
        {
            try
            {
                using var me = JsonDocument.Parse(meJson);
                var meData = me.RootElement.TryGetProperty("data", out var d) ? d : me.RootElement;
                email = ReadString(meData, "email", "userEmail");
            }
            catch (JsonException)
            {
                // Identity is best-effort; a malformed /me payload should not fail the usage fetch.
            }
        }

        if (email is null && planFromUsage is null)
        {
            return null;
        }

        return new ProviderIdentity
        {
            AccountEmail = email,
            Plan = string.IsNullOrWhiteSpace(planFromUsage) ? null : planFromUsage,
            LoginMethod = "Session cookie",
        };
    }

    private static double? ReadNumber(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ReadFirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property))
            {
                var value = ReadNumber(property);
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
