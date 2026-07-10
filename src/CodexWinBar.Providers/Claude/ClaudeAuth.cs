using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexWinBar.Core.Auth;
using CodexWinBar.Core.Http;

namespace CodexWinBar.Providers.Claude;

/// <summary>
/// In-app OAuth sign-in for Claude (Pro/Max subscription login), replicating Claude Code's
/// authorization-code + PKCE flow on an ephemeral localhost port. Tokens are stored in the app's
/// own credential store; nothing is read from the CLI's files.
/// </summary>
public static class ClaudeAuth
{
    /// <summary>Credential-store document name shared with <see cref="ClaudeCredentialStore"/>.</summary>
    public const string CredentialName = "claude";

    internal const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string CallbackPath = "/callback";

    // The usage endpoint requires user:profile; user:inference marks this as a subscription login.
    private const string Scope = "user:profile user:inference";

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the browser sign-in: opens the authorize URL, waits for the localhost callback,
    /// exchanges the code, and saves the tokens. Returns the signed-in account email when known.
    /// </summary>
    public static async Task<string?> SignInAsync(
        HttpClient http,
        AppCredentialStore store,
        Action<Uri> openBrowser,
        CancellationToken ct)
    {
        using var listener = new LoopbackHttpListener(0, CallbackPath);
        var redirectUri = $"http://localhost:{listener.Port}{CallbackPath}";
        var verifier = OAuthPkce.CreateVerifier();
        var state = OAuthPkce.CreateState();
        openBrowser(BuildAuthorizeUri(redirectUri, OAuthPkce.ChallengeS256(verifier), state));

        var query = await listener.WaitForCallbackAsync(
            parameters => RespondToCallback(parameters, state),
            ct).ConfigureAwait(false);
        var code = ValidateCallback(query, state);

        return await ExchangeCodeAsync(http, store, code, redirectUri, verifier, state, ct).ConfigureAwait(false);
    }

    private static Uri BuildAuthorizeUri(string redirectUri, string challenge, string state)
    {
        var query = new StringBuilder()
            .Append("code=true")
            .Append("&client_id=").Append(Uri.EscapeDataString(ClientId))
            .Append("&response_type=code")
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&code_challenge_method=S256")
            .Append("&state=").Append(Uri.EscapeDataString(state));
        return new Uri($"{AuthorizeUrl}?{query}");
    }

    private static CallbackResult RespondToCallback(IReadOnlyDictionary<string, string> parameters, string state)
    {
        if (parameters.TryGetValue("error", out var error))
        {
            // A real provider error/denial ends the flow; ValidateCallback surfaces it.
            return CallbackResult.Done(LoopbackResponse.Html(400, SignInPages.Failure("Claude", parameters.GetValueOrDefault("error_description") ?? error)));
        }

        if (!parameters.TryGetValue("state", out var echoed) || echoed != state || !parameters.ContainsKey("code"))
        {
            // Stray/racing request: answer it but keep waiting for the genuine callback.
            return CallbackResult.KeepWaiting(LoopbackResponse.Html(400, SignInPages.Failure("Claude", "The sign-in response was invalid.")));
        }

        return CallbackResult.Done(LoopbackResponse.Html(200, SignInPages.Success("Claude")));
    }

    private static string ValidateCallback(IReadOnlyDictionary<string, string> parameters, string state)
    {
        if (parameters.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException($"Claude sign-in was rejected: {parameters.GetValueOrDefault("error_description") ?? error}.");
        }

        if (!parameters.TryGetValue("state", out var echoed) || echoed != state)
        {
            throw new InvalidOperationException("Claude sign-in state mismatch; aborting.");
        }

        return parameters.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code)
            ? code
            : throw new InvalidOperationException("Claude sign-in returned no authorization code.");
    }

    private static async Task<string?> ExchangeCodeAsync(
        HttpClient http,
        AppCredentialStore store,
        string code,
        string redirectUri,
        string verifier,
        string state,
        CancellationToken ct)
    {
        // Claude's token endpoint takes a JSON body (not form-encoded) and echoes state back.
        var payload = new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["state"] = state,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = ProviderHttpClient.TimeoutCts(ct, ExchangeTimeout);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Claude token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = GetString(root, "access_token");
        var refreshToken = GetString(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Claude token exchange response was missing an access token.");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetDouble(out var seconds)
            ? seconds
            : (double?)null;
        var scopes = GetString(root, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        var oauth = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = refreshToken,
            ["expiresAt"] = expiresIn is > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).ToUnixTimeMilliseconds()
                : null,
            ["scopes"] = new JsonArray(scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray()),
        };

        // The response may carry subscription hints; keep them for the plan label when present.
        foreach (var (source, target) in new[] { ("subscription_type", "subscriptionType"), ("rate_limit_tier", "rateLimitTier") })
        {
            if (GetString(root, source) is { } value)
            {
                oauth[target] = value;
            }
        }

        store.Save(CredentialName, new JsonObject { ["claudeAiOauth"] = oauth }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return root.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object
            ? GetString(account, "email_address")
            : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
