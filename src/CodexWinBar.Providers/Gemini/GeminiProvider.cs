using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Gemini;

/// <summary>Gemini provider descriptor factory.</summary>
public static class GeminiProvider
{
    /// <summary>Creates the Gemini provider descriptor.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Gemini,
        Metadata = new ProviderMetadata
        {
            DisplayName = "Gemini",
            SessionLabel = "Pro",
            WeeklyLabel = "Flash",
            DefaultEnabled = false,
            DashboardUrl = new Uri("https://aistudio.google.com"),
        },
        Branding = new ProviderBranding { GlyphKey = "gemini", R = 66, G = 133, B = 244 },
        Strategies = [new GeminiOAuthFetchStrategy()],
    };
}

internal sealed class GeminiOAuthFetchStrategy : IFetchStrategy
{
    private static readonly Uri TokenUri = new("https://oauth2.googleapis.com/token");
    private static readonly Uri QuotaUri = new("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");
    private static readonly Uri LoadCodeAssistUri = new("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public string Id => "oauth";

    public FetchKind Kind => FetchKind.Oauth;

    public bool IsAvailable(FetchContext ctx) => ctx.Credentials.Exists(GeminiAuth.CredentialName);

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var rawCredentials = ctx.Credentials.Load(GeminiAuth.CredentialName) ??
            throw new UnauthorizedProviderException("Gemini is not signed in. Use Sign in on the Gemini card in Settings.");
        var credentials = LoadCredentials(rawCredentials);
        var accessToken = credentials.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken) || credentials.ExpiresAt <= ctx.Now().ToUniversalTime().AddMinutes(1))
        {
            accessToken = await RefreshAsync(ctx, rawCredentials, credentials.RefreshToken, ct).ConfigureAwait(false);
        }

        var projectId = await LoadProjectIdAsync(ctx.Http, accessToken, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, QuotaUri)
        {
            Content = new StringContent(
                projectId is null ? "{}" : JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = projectId }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var timeout = ProviderHttpClient.TimeoutCts(ct, Timeout);
        using var response = await ctx.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException($"Gemini OAuth credential was rejected with HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini quota request failed with HTTP {(int)response.StatusCode}.");
        }

        return GeminiQuotaParser.Parse(body, ctx.Now(), credentials.Email);
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static async Task<string> RefreshAsync(
        FetchContext ctx,
        string rawCredentials,
        string? refreshToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new UnauthorizedProviderException("Gemini refresh token is missing.");
        }

        var client = GeminiOAuthClientCredentials.Resolve(ctx.UserProfileDirectory) ??
            throw new InvalidOperationException("Could not locate Gemini CLI OAuth client configuration.");

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }),
        };

        using var timeout = ProviderHttpClient.TimeoutCts(ct, Timeout);
        using var response = await ctx.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException($"Gemini refresh token was rejected with HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        return PersistRefresh(ctx, rawCredentials, body);
    }

    private static async Task<string?> LoadProjectIdAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistUri)
        {
            Content = new StringContent(
                "{\"metadata\":{\"ideType\":\"GEMINI_CLI\",\"pluginType\":\"GEMINI\"}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var timeout = ProviderHttpClient.TimeoutCts(ct, Timeout);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return GeminiQuotaParser.ParseProjectId(body);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GeminiCredentials LoadCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new GeminiCredentials(
            GetString(root, "access_token"),
            GetString(root, "refresh_token"),
            GetExpiry(root),
            GetString(root, "email"));
    }

    private static string PersistRefresh(FetchContext ctx, string rawCredentials, string refreshJson)
    {
        var existing = JsonNode.Parse(rawCredentials)?.AsObject()
            ?? throw new InvalidOperationException("Gemini stored credentials are not a JSON object.");
        using var document = JsonDocument.Parse(refreshJson);
        var root = document.RootElement;
        var accessToken = GetString(root, "access_token")
            ?? throw new InvalidOperationException("Gemini refresh response omitted access_token.");

        existing["access_token"] = accessToken;
        if (GetString(root, "id_token") is { } idToken)
        {
            existing["id_token"] = idToken;
        }

        if (root.TryGetProperty("expires_in", out var expiresInElement) && expiresInElement.TryGetDouble(out var expiresIn))
        {
            existing["expiry_date"] = ctx.Now().ToUniversalTime().AddSeconds(expiresIn).ToUnixTimeMilliseconds();
        }

        ctx.Credentials.Save(GeminiAuth.CredentialName, existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return accessToken;
    }

    private static DateTimeOffset? GetExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expiry_date", out var expiry))
        {
            return null;
        }

        if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var number))
        {
            return number > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(number).ToUniversalTime()
                : DateTimeOffset.FromUnixTimeSeconds(number).ToUniversalTime();
        }

        if (expiry.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record GeminiCredentials(
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string? Email);
}

internal static class GeminiQuotaParser
{
    public static UsageSnapshot Parse(string json, DateTimeOffset now, string? email)
    {
        using var document = JsonDocument.Parse(json);
        var buckets = document.RootElement.TryGetProperty("buckets", out var bucketsElement)
            && bucketsElement.ValueKind == JsonValueKind.Array
                ? bucketsElement.EnumerateArray()
                : throw new InvalidOperationException("Gemini quota response omitted buckets.");

        var byModel = new Dictionary<string, GeminiBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets)
        {
            var modelId = GetString(bucket, "modelId", "model_id");
            var remainingFraction = GetDouble(bucket, "remainingFraction", "remaining_fraction");
            if (string.IsNullOrWhiteSpace(modelId) || remainingFraction is null)
            {
                continue;
            }

            var reset = GetString(bucket, "resetTime", "reset_time");
            var candidate = new GeminiBucket(modelId, Math.Clamp(remainingFraction.Value * 100, 0, 100), ParseReset(reset));
            if (!byModel.TryGetValue(modelId, out var existing) || candidate.PercentLeft < existing.PercentLeft)
            {
                byModel[modelId] = candidate;
            }
        }

        if (byModel.Count == 0)
        {
            throw new InvalidOperationException("Gemini quota response contained no model quota buckets.");
        }

        var all = byModel.Values.OrderBy(static bucket => bucket.ModelId, StringComparer.OrdinalIgnoreCase).ToList();
        var pro = Lowest(all.Where(static bucket => bucket.ModelId.Contains("pro", StringComparison.OrdinalIgnoreCase)));
        var flash = Lowest(all.Where(static bucket =>
            bucket.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase)
            && !bucket.ModelId.Contains("flash-lite", StringComparison.OrdinalIgnoreCase)));

        var primaryBucket = pro ?? Lowest(all)!;
        var secondaryBucket = flash;

        // The primary/secondary buckets are already rendered as the main windows; extras hold
        // only the remaining models so the flyout does not show the same bucket twice.
        var extraWindows = all
            .Where(bucket =>
                !string.Equals(bucket.ModelId, primaryBucket.ModelId, StringComparison.OrdinalIgnoreCase)
                && (secondaryBucket is null
                    || !string.Equals(bucket.ModelId, secondaryBucket.ModelId, StringComparison.OrdinalIgnoreCase)))
            .Select(bucket => new NamedRateWindow
            {
                Id = bucket.ModelId,
                Title = bucket.ModelId,
                Window = ToWindow(bucket),
            }).ToList();

        return new UsageSnapshot
        {
            Provider = ProviderId.Gemini,
            Primary = ToWindow(primaryBucket),
            Secondary = secondaryBucket is null ? null : ToWindow(secondaryBucket),
            ExtraWindows = extraWindows,
            Identity = new ProviderIdentity
            {
                AccountEmail = email,
                LoginMethod = "OAuth",
            },
            UpdatedAt = now.ToUniversalTime(),
            Confidence = DataConfidence.PercentOnly,
        };
    }

    public static string? ParseProjectId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("cloudaicompanionProject", out var project))
        {
            return null;
        }

        if (project.ValueKind == JsonValueKind.String)
        {
            return BlankToNull(project.GetString());
        }

        if (project.ValueKind == JsonValueKind.Object)
        {
            return BlankToNull(GetString(project, "id", "projectId"));
        }

        return null;
    }

    private static GeminiBucket? Lowest(IEnumerable<GeminiBucket> buckets) =>
        buckets.MinBy(static bucket => bucket.PercentLeft);

    private static RateWindow ToWindow(GeminiBucket bucket) => new()
    {
        UsedPercent = Math.Clamp(100 - bucket.PercentLeft, 0, 100),
        WindowMinutes = 1440,
        ResetsAt = bucket.ResetsAt,
    };

    private static DateTimeOffset? ParseReset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record GeminiBucket(string ModelId, double PercentLeft, DateTimeOffset? ResetsAt);
}

internal static partial class GeminiOAuthClientCredentials
{
    // The Gemini CLI's OAuth client (id + installed-app "secret") is NOT embedded here. Gemini is
    // deferred from the shipping catalog (Google restricts third-party use of the Code Assist API and
    // is deprecating the individual tier); its client identity is resolved from a local gemini-cli
    // install at runtime instead of being republished in this repo.
    private static readonly string[] RelativeOAuthPaths =
    [
        "node_modules/@google/gemini-cli/node_modules/@google/gemini-cli-core/dist/src/code_assist/oauth2.js",
        "node_modules/@google/gemini-cli-core/dist/src/code_assist/oauth2.js",
        "../gemini-cli-core/dist/src/code_assist/oauth2.js",
        "dist/src/code_assist/oauth2.js",
    ];

    public static OAuthClientCredentials? Resolve(string userProfileDirectory)
    {
        var candidates = CandidateRoots(userProfileDirectory);
        foreach (var root in candidates)
        {
            foreach (var relative in RelativeOAuthPaths)
            {
                var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(path) && TryParse(File.ReadAllText(path), out var credentials))
                {
                    return credentials;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots(string userProfileDirectory)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var gemini = Path.Combine(directory, OperatingSystem.IsWindows() ? "gemini.cmd" : "gemini");
            if (!File.Exists(gemini))
            {
                gemini = Path.Combine(directory, OperatingSystem.IsWindows() ? "gemini.ps1" : "gemini");
            }

            if (File.Exists(gemini))
            {
                var bin = Path.GetDirectoryName(Path.GetFullPath(gemini));
                if (bin is not null)
                {
                    yield return Path.GetFullPath(Path.Combine(bin, ".."));
                }
            }
        }

        yield return Path.Combine(userProfileDirectory, "AppData", "Roaming", "npm", "node_modules", "@google", "gemini-cli");
        yield return Path.Combine(userProfileDirectory, ".bun", "install", "global", "node_modules", "@google", "gemini-cli");
    }

    private static bool TryParse(string content, out OAuthClientCredentials credentials)
    {
        var clientId = OAuthClientIdRegex().Match(content);
        var clientSecret = OAuthClientSecretRegex().Match(content);
        if (!clientId.Success || !clientSecret.Success)
        {
            credentials = default;
            return false;
        }

        credentials = new OAuthClientCredentials(clientId.Groups[1].Value, clientSecret.Groups[1].Value);
        return true;
    }

    [GeneratedRegex("""(?:const|let|var)?\s*OAUTH_CLIENT_ID\s*=\s*['"]([\w\-.]+)['"]\s*;""")]
    private static partial Regex OAuthClientIdRegex();

    [GeneratedRegex("""(?:const|let|var)?\s*OAUTH_CLIENT_SECRET\s*=\s*['"]([\w\-]+)['"]\s*;""")]
    private static partial Regex OAuthClientSecretRegex();
}

internal readonly record struct OAuthClientCredentials(string ClientId, string ClientSecret);
