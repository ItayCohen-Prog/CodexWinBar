using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Claude;

/// <summary>Claude provider descriptor factory.</summary>
public static class ClaudeProvider
{
    /// <summary>Creates the Claude provider descriptor used by the provider catalog.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Claude,
        Metadata = new ProviderMetadata
        {
            DisplayName = "Claude",
            SessionLabel = "Session",
            WeeklyLabel = "Weekly",
            DefaultEnabled = false,
            DashboardUrl = new Uri("https://claude.ai/settings/usage"),
            StatusPageUrl = new Uri("https://status.anthropic.com"),
        },
        Branding = new ProviderBranding { GlyphKey = "claude", R = 217, G = 119, B = 87 },
        Strategies = [new ClaudeOAuthFetchStrategy()],
    };
}

internal sealed class ClaudeOAuthFetchStrategy : IFetchStrategy
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Id => "oauth";

    public FetchKind Kind => FetchKind.Oauth;

    public bool IsAvailable(FetchContext ctx) => ctx.Credentials.Exists(ClaudeAuth.CredentialName);

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var credentials = ClaudeCredentialStore.Load(ctx);
        if (!credentials.Scopes.Contains("user:profile", StringComparer.Ordinal))
        {
            throw new UnauthorizedProviderException("Claude OAuth token cannot call usage because it lacks user:profile scope.");
        }

        if (credentials.IsExpired(ctx.Now()))
        {
            credentials = await RefreshAsync(ctx, credentials, ct).ConfigureAwait(false);
        }

        string usageJson;
        try
        {
            usageJson = await GetUsageAsync(credentials, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedProviderException) when (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            // The token may be revoked, or stale without a recorded expiry (IsExpired treats an
            // unknown expiry as valid); mirror Codex's 401 -> refresh -> retry path before
            // surfacing an auth error.
            credentials = await RefreshAsync(ctx, credentials, ct).ConfigureAwait(false);
            usageJson = await GetUsageAsync(credentials, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(credentials.SubscriptionType) && string.IsNullOrWhiteSpace(credentials.RateLimitTier))
        {
            credentials = await BackfillPlanFromProfileAsync(ctx, credentials, ct).ConfigureAwait(false);
        }

        var snapshot = ClaudeUsageParser.Parse(usageJson, ctx.Now(), credentials.SubscriptionType, credentials.RateLimitTier);
        if (snapshot.Primary is not null || snapshot.Secondary is not null || snapshot.Tertiary is not null ||
            snapshot.ExtraWindows.Count > 0 || snapshot.Credits is not null)
        {
            return snapshot;
        }

        throw new InvalidOperationException("Claude usage response contained no usable usage windows or credits.");
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static async Task<ClaudeCredentials> RefreshAsync(
        FetchContext ctx,
        ClaudeCredentials credentials,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new UnauthorizedProviderException("Claude OAuth token expired and no refresh token is available.");
        }

        using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://platform.claude.com/v1/oauth/token");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", credentials.RefreshToken),
            new KeyValuePair<string, string>("client_id", ClaudeAuth.ClientId),
        ]);

        using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            throw new UnauthorizedProviderException("Claude OAuth refresh token is expired or revoked.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Claude OAuth refresh failed with HTTP {(int)response.StatusCode}.");
        }

        var refreshed = ClaudeCredentialStore.MergeRefreshResponse(credentials, body, ctx.Now());
        ClaudeCredentialStore.Save(ctx, refreshed);
        return refreshed;
    }

    /// <summary>
    /// The in-app OAuth flow stores only tokens — unlike the CLI's credential file, the token
    /// exchange rarely returns subscription_type/rate_limit_tier, so the card subtitle degraded to
    /// the "OAuth" login-method fallback. Fetches the plan fields once from the OAuth profile
    /// endpoint and persists them alongside the tokens; failures leave the credentials unchanged
    /// (the plan label is cosmetic and must never break the usage fetch).
    /// </summary>
    private static async Task<ClaudeCredentials> BackfillPlanFromProfileAsync(
        FetchContext ctx,
        ClaudeCredentials credentials,
        CancellationToken ct)
    {
        try
        {
            using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return credentials;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("organization", out var organization) ||
                organization.ValueKind != JsonValueKind.Object)
            {
                return credentials;
            }

            var subscriptionType = ReadNonEmptyString(organization, "organization_type");
            var rateLimitTier = ReadNonEmptyString(organization, "rate_limit_tier");
            if (subscriptionType is null && rateLimitTier is null)
            {
                return credentials;
            }

            var updated = credentials with { SubscriptionType = subscriptionType, RateLimitTier = rateLimitTier };
            ClaudeCredentialStore.Save(ctx, updated);
            return updated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return credentials;
        }
    }

    private static string? ReadNonEmptyString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static async Task<string> GetUsageAsync(ClaudeCredentials credentials, CancellationToken ct)
    {
        using var timeout = ProviderHttpClient.TimeoutCts(ct, RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("Claude OAuth token is expired or unauthorized.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Claude usage request failed with HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }
}

internal static class ClaudeCredentialStore
{
    public static ClaudeCredentials Load(FetchContext ctx)
    {
        var json = ctx.Credentials.Load(ClaudeAuth.CredentialName) ??
            throw new UnauthorizedProviderException("Claude is not signed in. Use Sign in on the Claude card in Settings.");

        JsonNode root;
        try
        {
            root = JsonNode.Parse(json) ?? throw new JsonException("empty document");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Claude stored credentials could not be parsed.", ex);
        }

        var oauth = root["claudeAiOauth"]?.AsObject();
        if (oauth is null)
        {
            throw new UnauthorizedProviderException("Claude stored credentials do not contain claudeAiOauth. Sign in again from Settings.");
        }

        var accessToken = ReadString(oauth["accessToken"]);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new UnauthorizedProviderException("Claude credentials file does not contain an access token.");
        }

        return new ClaudeCredentials(
            accessToken.Trim(),
            ReadString(oauth["refreshToken"])?.Trim(),
            ReadEpochMilliseconds(oauth["expiresAt"]),
            ReadStringArray(oauth["scopes"]),
            ReadString(oauth["subscriptionType"])?.Trim(),
            ReadString(oauth["rateLimitTier"])?.Trim(),
            root);
    }

    public static ClaudeCredentials MergeRefreshResponse(
        ClaudeCredentials existing,
        string responseJson,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var accessToken = ReadString(GetProperty(root, "access_token"));
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Claude OAuth refresh response did not include an access token.");
        }

        var expiresIn = ReadDouble(GetProperty(root, "expires_in"));
        var expiresAt = expiresIn is > 0 ? now.ToUniversalTime().AddSeconds(expiresIn.Value) : existing.ExpiresAt;
        return existing with
        {
            AccessToken = accessToken.Trim(),
            RefreshToken = ReadString(GetProperty(root, "refresh_token"))?.Trim() ?? existing.RefreshToken,
            ExpiresAt = expiresAt,
        };
    }

    public static void Save(FetchContext ctx, ClaudeCredentials credentials)
    {
        var root = credentials.RawRoot.DeepClone().AsObject();
        var oauth = root["claudeAiOauth"] as JsonObject ?? [];
        oauth["accessToken"] = credentials.AccessToken;
        oauth["refreshToken"] = credentials.RefreshToken;
        oauth["expiresAt"] = credentials.ExpiresAt?.ToUnixTimeMilliseconds();
        oauth["scopes"] = new JsonArray(credentials.Scopes.Select(scope => JsonValue.Create(scope)).ToArray());
        oauth["subscriptionType"] = credentials.SubscriptionType;
        oauth["rateLimitTier"] = credentials.RateLimitTier;
        root["claudeAiOauth"] = oauth;

        ctx.Credentials.Save(ClaudeAuth.CredentialName, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? ReadString(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String
        ? node.GetValue<string>()
        : null;

    private static string? ReadString(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static DateTimeOffset? ReadEpochMilliseconds(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        double? value = node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>(),
            JsonValueKind.String when double.TryParse(
                node.GetValue<string>(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
        return value is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value).ToUniversalTime() : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array.Select(ReadString).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim())
            .ToArray();
    }

    private static JsonElement? GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static double? ReadDouble(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

}

internal sealed record ClaudeCredentials(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes,
    string? SubscriptionType,
    string? RateLimitTier,
    JsonNode RawRoot)
{
    // A credential with no recorded expiry is assumed still valid rather than perpetually
    // expired; treating null as expired forced a full OAuth refresh + credential rewrite on
    // every fetch when the refresh response also omitted expires_in. A genuinely stale token
    // is caught by the 401 -> refresh -> retry path in FetchAsync instead.
    public bool IsExpired(DateTimeOffset now) => this.ExpiresAt is { } expiresAt && now.ToUniversalTime() >= expiresAt;
}

internal static class ClaudeUsageParser
{
    public static UsageSnapshot Parse(string rawJson, DateTimeOffset now, string? subscriptionType, string? rateLimitTier)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var fiveHour = ParseWindow(GetProperty(root, "five_hour"), 300);
        var sevenDay = ParseWindow(GetProperty(root, "seven_day"), 10080);
        RateWindow? primary;
        RateWindow? secondary;
        if (fiveHour is not null)
        {
            primary = fiveHour;
            secondary = sevenDay;
        }
        else
        {
            primary = sevenDay;
            secondary = null;
        }

        var extras = new List<NamedRateWindow>();

        AddExtra(extras, "claude-sonnet-weekly", "Sonnet weekly", ParseWindow(GetProperty(root, "seven_day_sonnet"), 10080));
        AddExtra(
            extras,
            "claude-routines",
            "Routines",
            ParseFirstWindow(root, 10080, "seven_day_routines", "seven_day_cowork"));

        return new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            Primary = primary,
            Secondary = secondary,
            Tertiary = ParseWindow(GetProperty(root, "seven_day_opus"), 10080),
            ExtraWindows = extras,
            Credits = ParseExtraUsage(root, now),
            Identity = new ProviderIdentity
            {
                Plan = PlanLabel(subscriptionType, rateLimitTier),
                LoginMethod = "OAuth",
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

        extras.Add(new NamedRateWindow
        {
            Id = id,
            Title = title,
            Window = window,
        });
    }

    private static CreditsSnapshot? ParseExtraUsage(JsonElement root, DateTimeOffset now)
    {
        var extra = GetProperty(root, "extra_usage");
        if (extra is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var isEnabled = ReadBool(GetProperty(extra.Value, "is_enabled"));
        if (isEnabled is false)
        {
            return null;
        }

        var used = ReadDouble(GetProperty(extra.Value, "used_credits"));
        var limit = ReadDouble(GetProperty(extra.Value, "monthly_limit"));
        if (used is null && limit is null)
        {
            return null;
        }

        return new CreditsSnapshot
        {
            Remaining = Math.Max(0, (limit ?? 0) - (used ?? 0)),
            Limit = limit,
            Unit = ReadString(GetProperty(extra.Value, "currency")) ?? "credits",
            UpdatedAt = now.ToUniversalTime(),
        };
    }

    private static RateWindow? ParseFirstWindow(JsonElement root, int windowMinutes, params string[] names)
    {
        foreach (var name in names)
        {
            var window = ParseWindow(GetProperty(root, name), windowMinutes);
            if (window is not null)
            {
                return window;
            }
        }

        return null;
    }

    private static RateWindow? ParseWindow(JsonElement? value, int windowMinutes)
    {
        if (value is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var utilization = ReadDouble(GetProperty(value.Value, "utilization"));
        if (utilization is null)
        {
            return null;
        }

        return new RateWindow
        {
            UsedPercent = ClampPercent(utilization.Value),
            WindowMinutes = windowMinutes,
            ResetsAt = ParseReset(GetProperty(value.Value, "resets_at")),
        };
    }

    private static DateTimeOffset? ParseReset(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)number).ToUniversalTime()
                : DateTimeOffset.FromUnixTimeSeconds((long)number).ToUniversalTime();
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)numeric).ToUniversalTime()
                    : DateTimeOffset.FromUnixTimeSeconds((long)numeric).ToUniversalTime();
            }

            if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static string? PlanLabel(string? subscriptionType, string? rateLimitTier)
    {
        // Try each source for a known plan independently (upstream ClaudePlan.fromOAuthCredentials):
        // an unrecognized subscriptionType must not shadow a recognizable rateLimitTier.
        if (KnownPlan(subscriptionType) is { } fromSubscription)
        {
            return fromSubscription;
        }

        if (KnownPlan(rateLimitTier) is { } fromTier)
        {
            return fromTier;
        }

        var source = FirstNonEmpty(subscriptionType, rateLimitTier);
        return source is null
            ? null
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(source.Replace('_', ' ').Replace('-', ' '));
    }

    /// <summary>
    /// Substring plan match, because the real values are decorated: subscriptionType "max_20x" or
    /// "claude_max", rateLimitTier "default_claude_max_20x" (upstream ClaudePlan matches the same way).
    /// </summary>
    private static string? KnownPlan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("max"))
        {
            return "Max";
        }

        if (normalized.Contains("pro"))
        {
            return "Pro";
        }

        if (normalized.Contains("team"))
        {
            return "Team";
        }

        if (normalized.Contains("enterprise"))
        {
            return "Enterprise";
        }

        return normalized.Contains("ultra") ? "Ultra" : null;
    }

    private static JsonElement? GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? ReadString(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static double? ReadDouble(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static double ClampPercent(double value) => Math.Min(100, Math.Max(0, value));
}
