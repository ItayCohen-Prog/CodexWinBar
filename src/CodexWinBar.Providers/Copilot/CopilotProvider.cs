using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Copilot;

/// <summary>GitHub Copilot provider descriptor factory.</summary>
public static class CopilotProvider
{
    /// <summary>Creates the GitHub Copilot provider descriptor.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Copilot,
        Metadata = new ProviderMetadata
        {
            DisplayName = "GitHub Copilot",
            SessionLabel = "Premium",
            WeeklyLabel = "Chat",
            DefaultEnabled = false,
            DashboardUrl = new Uri("https://github.com/settings/copilot"),
            StatusPageUrl = new Uri("https://www.githubstatus.com"),
        },
        Branding = new ProviderBranding { GlyphKey = "copilot", R = 36, G = 41, B = 47 },
        Strategies = [new CopilotOAuthFetchStrategy()],
    };
}

internal sealed class CopilotOAuthFetchStrategy : IFetchStrategy
{
    private static readonly Uri UsageUri = new("https://api.github.com/copilot_internal/user");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public string Id => "oauth";

    public FetchKind Kind => FetchKind.Oauth;

    public bool IsAvailable(FetchContext ctx) => !string.IsNullOrWhiteSpace(ctx.ProviderConfig.ApiKey);

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var token = ctx.ProviderConfig.ApiKey?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new UnauthorizedProviderException("Copilot OAuth token is missing.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexBar");

        using var timeout = ProviderHttpClient.TimeoutCts(ct, Timeout);
        using var response = await ctx.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new UnauthorizedProviderException($"Copilot OAuth token was rejected with HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Copilot usage request failed with HTTP {(int)response.StatusCode}.");
        }

        return CopilotUsageParser.Parse(body, ctx.Now());
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;
}

internal static class CopilotUsageParser
{
    public static UsageSnapshot Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var snapshots = TryGetProperty(root, "quota_snapshots", "quotaSnapshots");
        var reset = TryGetString(root, "quota_reset_date", "quotaResetDate");
        var resetsAt = ParseReset(reset);

        var premium = snapshots is null
            ? null
            : ParseWindow(TryGetProperty(snapshots.Value, "premium_interactions", "premiumInteractions"), resetsAt);
        var chat = snapshots is null ? null : ParseWindow(TryGetProperty(snapshots.Value, "chat"), resetsAt);
        var completions = snapshots is null ? null : ParseWindow(TryGetProperty(snapshots.Value, "completions"), resetsAt);

        var extras = new List<NamedRateWindow>();
        AddExtra(extras, "chat", "Chat", chat);
        AddExtra(extras, "completions", "Completions", completions);

        return new UsageSnapshot
        {
            Provider = ProviderId.Copilot,
            Primary = premium,
            Secondary = chat,
            ExtraWindows = extras,
            Identity = new ProviderIdentity
            {
                Plan = TryGetString(root, "copilot_plan", "copilotPlan"),
                LoginMethod = "Device flow",
            },
            UpdatedAt = now.ToUniversalTime(),
            Confidence = DataConfidence.Exact,
        };
    }

    private static void AddExtra(List<NamedRateWindow> extras, string id, string title, RateWindow? window)
    {
        if (window is null)
        {
            return;
        }

        extras.Add(new NamedRateWindow { Id = id, Title = title, Window = window });
    }

    private static RateWindow? ParseWindow(JsonElement? element, DateTimeOffset? resetsAt)
    {
        if (element is null || IsUnlimited(element.Value) || IsPlaceholder(element.Value))
        {
            return null;
        }

        var percentRemaining = TryGetDouble(element.Value, "percent_remaining", "percentRemaining");
        if (percentRemaining is null)
        {
            return null;
        }

        return new RateWindow
        {
            UsedPercent = Clamp(100 - percentRemaining.Value),
            ResetsAt = resetsAt,
        };
    }

    private static bool IsUnlimited(JsonElement element)
    {
        if (TryGetBool(element, "unlimited") == true)
        {
            return true;
        }

        var entitlement = TryGetDouble(element, "entitlement");
        var remaining = TryGetDouble(element, "remaining");
        return entitlement is null or <= 0 && remaining is null;
    }

    private static bool IsPlaceholder(JsonElement element)
    {
        var entitlement = TryGetDouble(element, "entitlement");
        var remaining = TryGetDouble(element, "remaining");
        var percentRemaining = TryGetDouble(element, "percent_remaining", "percentRemaining");
        return entitlement == 0 && remaining == 0 && percentRemaining == 100;
    }

    private static DateTimeOffset? ParseReset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            return instant.ToUniversalTime();
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        }

        return null;
    }

    private static JsonElement? TryGetProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        var value = TryGetProperty(element, names);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    private static double? TryGetDouble(JsonElement element, params string[] names)
    {
        var value = TryGetProperty(element, names);
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.Value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => null,
        };
    }

    private static bool? TryGetBool(JsonElement element, params string[] names)
    {
        var value = TryGetProperty(element, names);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static double Clamp(double value) => Math.Min(100, Math.Max(0, value));
}
