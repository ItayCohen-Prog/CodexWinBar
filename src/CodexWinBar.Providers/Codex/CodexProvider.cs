using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexWinBar.Core.Http;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Codex;

/// <summary>Codex provider descriptor factory.</summary>
public static class CodexProvider
{
    /// <summary>Creates the Codex provider descriptor used by the provider catalog.</summary>
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Codex,
        Metadata = new ProviderMetadata
        {
            DisplayName = "Codex",
            SessionLabel = "5h",
            WeeklyLabel = "Weekly",
            DefaultEnabled = true,
            SupportsCredits = true,
            DashboardUrl = new Uri("https://chatgpt.com/codex"),
            StatusPageUrl = new Uri("https://status.openai.com"),
        },
        Branding = new ProviderBranding { GlyphKey = "codex", R = 30, G = 63, B = 69 },
        Strategies = [new CodexOAuthFetchStrategy()],
    };
}

internal sealed class CodexOAuthFetchStrategy : IFetchStrategy
{
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetCreditsTimeout = TimeSpan.FromSeconds(4);

    public string Id => "oauth";

    public FetchKind Kind => FetchKind.Oauth;

    public bool IsAvailable(FetchContext ctx) => File.Exists(CodexCredentialStore.GetAuthPath(ctx));

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var path = CodexCredentialStore.GetAuthPath(ctx);
        var credentials = CodexCredentialStore.Load(path);

        if (CodexCredentialStore.ShouldRefresh(credentials, ctx.Now()))
        {
            credentials = await RefreshAsync(ctx, credentials, path, ct).ConfigureAwait(false);
        }

        var usageJson = await GetUsageAsync(ctx, credentials, ct).ConfigureAwait(false);
        CodexResetCreditsResult? resetCredits = null;
        try
        {
            var resetCreditsJson = await GetResetCreditsAsync(ctx, credentials, ct).ConfigureAwait(false);
            resetCredits = CodexUsageParser.ParseResetCredits(resetCreditsJson);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Log("Codex reset credits request timed out.");
        }
        catch (Exception ex) when (ex is not UnauthorizedProviderException)
        {
            ctx.Log($"Codex reset credits unavailable: {ex.GetType().Name}.");
        }

        var snapshot = CodexUsageParser.ParseUsage(
            usageJson,
            ctx.Now(),
            credentials.IdToken,
            resetCredits);

        if (snapshot.Primary is not null || snapshot.Secondary is not null || snapshot.Credits is not null ||
            snapshot.ExtraWindows.Count > 0)
        {
            return snapshot;
        }

        throw new InvalidOperationException("Codex usage response contained no rate limits or credits.");
    }

    public bool ShouldFallback(Exception error, FetchContext ctx) => false;

    private static async Task<CodexCredentials> RefreshAsync(
        FetchContext ctx,
        CodexCredentials credentials,
        string path,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            return credentials;
        }

        using var timeout = ProviderHttpClient.TimeoutCts(ct, TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token");
        request.Content = JsonContent(new JsonObject
        {
            ["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
            ["scope"] = "openid profile email",
        });

        using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("Codex OAuth refresh token is expired or revoked.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Codex OAuth refresh failed with HTTP {(int)response.StatusCode}.");
        }

        var refreshed = CodexCredentialStore.MergeRefreshResponse(credentials, body, ctx.Now());
        CodexCredentialStore.Save(path, refreshed, ctx.Log);
        return refreshed;
    }

    private static async Task<string> GetUsageAsync(FetchContext ctx, CodexCredentials credentials, CancellationToken ct)
    {
        try
        {
            return await SendJsonGetAsync(
                credentials,
                "https://chatgpt.com/backend-api/wham/usage",
                UsageTimeout,
                useUpperAccountId: false,
                includeResetCreditHeaders: false,
                ct).ConfigureAwait(false);
        }
        catch (UnauthorizedProviderException) when (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            var refreshed = await RefreshAsync(ctx, credentials, CodexCredentialStore.GetAuthPath(ctx), ct)
                .ConfigureAwait(false);
            return await SendJsonGetAsync(
                refreshed,
                "https://chatgpt.com/backend-api/wham/usage",
                UsageTimeout,
                useUpperAccountId: false,
                includeResetCreditHeaders: false,
                ct).ConfigureAwait(false);
        }
    }

    private static Task<string> GetResetCreditsAsync(
        FetchContext ctx,
        CodexCredentials credentials,
        CancellationToken ct) =>
        SendJsonGetAsync(
            credentials,
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits",
            ResetCreditsTimeout,
            useUpperAccountId: true,
            includeResetCreditHeaders: true,
            ct);

    private static async Task<string> SendJsonGetAsync(
        CodexCredentials credentials,
        string url,
        TimeSpan timeoutValue,
        bool useUpperAccountId,
        bool includeResetCreditHeaders,
        CancellationToken ct)
    {
        using var timeout = ProviderHttpClient.TimeoutCts(ct, timeoutValue);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd("CodexBar");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation(
                useUpperAccountId ? "ChatGPT-Account-ID" : "ChatGPT-Account-Id",
                credentials.AccountId);
        }

        if (includeResetCreditHeaders)
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");
        }

        using var response = await ProviderHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedProviderException("Codex OAuth token is expired or unauthorized.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Codex API request failed with HTTP {(int)response.StatusCode}.");
        }

        return body;
    }

    private static StringContent JsonContent(JsonObject payload) =>
        new(payload.ToJsonString(), Encoding.UTF8, "application/json");
}

internal static class CodexCredentialStore
{
    private static readonly TimeSpan RefreshAge = TimeSpan.FromDays(8);

    public static string GetAuthPath(FetchContext ctx)
    {
        var codexHome = ctx.Environment("CODEX_HOME");
        var basePath = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(ctx.UserProfileDirectory, ".codex")
            : codexHome.Trim();
        return Path.Combine(basePath, "auth.json");
    }

    public static CodexCredentials Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new UnauthorizedProviderException("Codex auth.json was not found. Run codex to sign in.");
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) ??
                throw new JsonException("empty document");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Codex auth.json could not be parsed.", ex);
        }

        var apiKey = ReadString(root["OPENAI_API_KEY"]);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return new CodexCredentials(apiKey.Trim(), string.Empty, null, null, null, root);
        }

        var tokens = root["tokens"];
        var accessToken = ReadString(tokens?["access_token"]) ?? ReadString(tokens?["accessToken"]);
        var refreshToken = ReadString(tokens?["refresh_token"]) ?? ReadString(tokens?["refreshToken"]);
        if (string.IsNullOrWhiteSpace(accessToken) || refreshToken is null)
        {
            throw new UnauthorizedProviderException("Codex auth.json exists but contains no usable tokens.");
        }

        return new CodexCredentials(
            accessToken.Trim(),
            refreshToken,
            ReadString(tokens?["id_token"]) ?? ReadString(tokens?["idToken"]),
            ReadString(tokens?["account_id"]) ?? ReadString(tokens?["accountId"]),
            ReadDateTime(root["last_refresh"]),
            root);
    }

    public static CodexCredentials MergeRefreshResponse(
        CodexCredentials existing,
        string responseJson,
        DateTimeOffset now)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(responseJson) ?? throw new JsonException("empty document");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Codex OAuth refresh response could not be parsed.", ex);
        }

        return existing with
        {
            AccessToken = ReadString(root["access_token"]) ?? existing.AccessToken,
            RefreshToken = ReadString(root["refresh_token"]) ?? existing.RefreshToken,
            IdToken = ReadString(root["id_token"]) ?? existing.IdToken,
            LastRefresh = now.ToUniversalTime(),
        };
    }

    public static void Save(string path, CodexCredentials credentials, Action<string>? log = null)
    {
        var root = credentials.RawRoot.DeepClone().AsObject();
        root["OPENAI_API_KEY"] = null;
        root["tokens"] = new JsonObject
        {
            ["access_token"] = credentials.AccessToken,
            ["refresh_token"] = credentials.RefreshToken,
            ["id_token"] = credentials.IdToken,
            ["account_id"] = credentials.AccountId,
        };
        root["last_refresh"] = credentials.LastRefresh?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }

            TryRestrictAcl(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            log?.Invoke($"Codex credential write-back skipped. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string? ReadString(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String
        ? node.GetValue<string>()
        : null;

    private static DateTimeOffset? ReadDateTime(JsonNode? node)
    {
        var value = ReadString(node);
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryRestrictAcl(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return;
            }

            var security = new FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception)
        {
            // Best-effort hardening; fetch should not fail after a successful refresh because ACL tightening failed.
        }
    }

    public static bool ShouldRefresh(CodexCredentials credentials, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(credentials.RefreshToken) &&
        (credentials.LastRefresh is null || now.ToUniversalTime() - credentials.LastRefresh.Value.ToUniversalTime() > RefreshAge);
}

internal sealed record CodexCredentials(
    string AccessToken,
    string RefreshToken,
    string? IdToken,
    string? AccountId,
    DateTimeOffset? LastRefresh,
    JsonNode RawRoot);

internal static class CodexUsageParser
{
    public static UsageSnapshot ParseUsage(
        string rawJson,
        DateTimeOffset now,
        string? idToken,
        CodexResetCreditsResult? resetCredits)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var rateLimit = GetProperty(root, "rate_limit");
        var primary = rateLimit is { } rl ? ParseWindow(GetProperty(rl, "primary_window"), now) : null;
        var secondary = rateLimit is { } rl2 ? ParseWindow(GetProperty(rl2, "secondary_window"), now) : null;
        (primary, secondary) = Normalize(primary, secondary);

        var extras = ParseAdditionalWindows(root, now);
        if (resetCredits is { AvailableCount: > 0 })
        {
            extras.Add(new NamedRateWindow
            {
                Id = "codex-reset-credits",
                Title = "Reset credits",
                UsageKnown = false,
                Window = new RateWindow
                {
                    UsedPercent = 0,
                    ResetDescription = $"{resetCredits.AvailableCount} available",
                },
            });
        }

        var credits = ParseCredits(root, now);
        var plan = ReadString(GetProperty(root, "plan_type"));
        var identity = ParseIdentity(idToken, plan);

        return new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Primary = primary,
            Secondary = secondary,
            ExtraWindows = extras,
            Credits = credits,
            Identity = identity,
            UpdatedAt = now.ToUniversalTime(),
            Confidence = DataConfidence.Exact,
        };
    }

    public static CodexResetCreditsResult ParseResetCredits(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var count = ReadDouble(GetProperty(root, "available_count"));
        if (count is null or < 0)
        {
            throw new InvalidOperationException("Codex reset credits response did not include a valid available_count.");
        }

        return new CodexResetCreditsResult((int)Math.Floor(count.Value));
    }

    private static CreditsSnapshot? ParseCredits(JsonElement root, DateTimeOffset now)
    {
        var credits = GetProperty(root, "credits");
        var balance = credits is { } c ? ReadDouble(GetProperty(c, "balance")) : null;
        if (balance is null)
        {
            return null;
        }

        var limit = ParseIndividualLimit(root);
        return new CreditsSnapshot
        {
            Remaining = balance.Value,
            Limit = limit,
            UpdatedAt = now.ToUniversalTime(),
        };
    }

    private static double? ParseIndividualLimit(JsonElement root)
    {
        var individual = GetProperty(root, "individual_limit") ?? GetProperty(root, "individualLimit");
        if (individual is null && GetProperty(root, "rate_limit") is { } rateLimit)
        {
            individual = GetProperty(rateLimit, "individual_limit") ?? GetProperty(rateLimit, "individualLimit");
        }

        if (individual is null)
        {
            return null;
        }

        var limit = ReadDouble(GetProperty(individual.Value, "limit"));
        return limit is > 0 ? limit : null;
    }

    private static List<NamedRateWindow> ParseAdditionalWindows(JsonElement root, DateTimeOffset now)
    {
        var results = new List<NamedRateWindow>();
        var limits = GetProperty(root, "additional_rate_limits");
        if (limits is not { ValueKind: JsonValueKind.Array })
        {
            return results;
        }

        foreach (var item in limits.Value.EnumerateArray())
        {
            var limitName = ReadString(GetProperty(item, "limit_name"));
            var feature = ReadString(GetProperty(item, "metered_feature"));
            var rateLimit = GetProperty(item, "rate_limit");
            if (rateLimit is null)
            {
                continue;
            }

            var first = ParseWindow(GetProperty(rateLimit.Value, "primary_window"), now) ??
                ParseWindow(GetProperty(rateLimit.Value, "secondary_window"), now);
            if (first is null)
            {
                continue;
            }

            var title = FirstNonEmpty(limitName, feature) ?? "Codex extra limit";
            results.Add(new NamedRateWindow
            {
                Id = "codex-" + Slug(FirstNonEmpty(feature, limitName) ?? "extra-limit"),
                Title = title,
                Window = first,
            });
        }

        return results;
    }

    private static ProviderIdentity? ParseIdentity(string? idToken, string? responsePlan)
    {
        var payload = ParseJwtPayload(idToken);
        var email = ReadString(GetProperty(payload, "email")) ??
            ReadString(GetProperty(payload, "https://api.openai.com/profile.email"));
        var plan = FirstNonEmpty(
            responsePlan,
            ReadString(GetProperty(payload, "https://api.openai.com/auth.chatgpt_plan_type")),
            ReadString(GetProperty(payload, "chatgpt_plan_type")));

        if (email is null && plan is null)
        {
            return null;
        }

        return new ProviderIdentity
        {
            AccountEmail = email,
            Plan = plan,
            LoginMethod = "OAuth",
        };
    }

    private static JsonElement ParseJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return default;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return default;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static RateWindow? ParseWindow(JsonElement? value, DateTimeOffset now)
    {
        if (value is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var used = ReadDouble(GetProperty(value.Value, "used_percent")) ?? ReadDouble(GetProperty(value.Value, "usedPercent"));
        if (used is null)
        {
            return null;
        }

        var windowSeconds = ReadDouble(GetProperty(value.Value, "limit_window_seconds")) ??
            ReadDouble(GetProperty(value.Value, "limitWindowSeconds"));
        var resetAt = ReadDouble(GetProperty(value.Value, "reset_at")) ?? ReadDouble(GetProperty(value.Value, "resets_at")) ??
            ReadDouble(GetProperty(value.Value, "resetsAt"));

        return new RateWindow
        {
            UsedPercent = ClampPercent(used.Value),
            WindowMinutes = windowSeconds is > 0 ? (int)Math.Round(windowSeconds.Value / 60, MidpointRounding.AwayFromZero) : null,
            ResetsAt = resetAt is > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)resetAt.Value).ToUniversalTime()
                : ParseResetAfter(value.Value, now),
        };
    }

    private static DateTimeOffset? ParseResetAfter(JsonElement window, DateTimeOffset now)
    {
        var resetAfter = ReadDouble(GetProperty(window, "reset_after")) ?? ReadDouble(GetProperty(window, "resetAfter"));
        return resetAfter is > 0 ? now.ToUniversalTime().AddSeconds(resetAfter.Value) : null;
    }

    private static (RateWindow? Primary, RateWindow? Secondary) Normalize(RateWindow? primary, RateWindow? secondary)
    {
        if (primary?.WindowMinutes == 10080 && secondary?.WindowMinutes == 300)
        {
            return (secondary, primary);
        }

        if (primary?.WindowMinutes == 10080 && secondary is null)
        {
            return (null, primary);
        }

        if (secondary?.WindowMinutes == 300 && primary is null)
        {
            return (secondary, null);
        }

        return (primary, secondary);
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.TryGetProperty(name, out var value) ? value : null;
    }

    private static string? ReadString(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
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

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Slug(string value)
    {
        var builder = new StringBuilder();
        var previousDash = false;
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
                previousDash = false;
            }
            else if (!previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static double ClampPercent(double value) => Math.Min(100, Math.Max(0, value));
}

internal sealed record CodexResetCreditsResult(int AvailableCount);
