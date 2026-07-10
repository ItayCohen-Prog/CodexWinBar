using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexWinBar.Core.Auth;
using CodexWinBar.Core.Http;

namespace CodexWinBar.Providers.Codex;

/// <summary>
/// In-app OAuth sign-in for Codex, replicating the flow of the open-source Codex CLI
/// (auth.openai.com authorization-code + PKCE on the registered localhost ports). The resulting
/// tokens are stored in the app's own credential store; nothing is read from the CLI's files.
/// </summary>
public static class CodexAuth
{
    /// <summary>Credential-store document name shared with <see cref="CodexCredentialStore"/>.</summary>
    public const string CredentialName = "codex";

    internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string Issuer = "https://auth.openai.com";
    private const string CallbackPath = "/auth/callback";
    private const string Scope = "openid profile email offline_access";

    // The OAuth client's registered redirect URIs only cover these two localhost ports.
    private static readonly int[] Ports = [1455, 1457];
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
        using var listener = StartListener();
        var redirectUri = $"http://localhost:{listener.Port}{CallbackPath}";
        var verifier = OAuthPkce.CreateVerifier();
        var state = OAuthPkce.CreateState();
        openBrowser(BuildAuthorizeUri(redirectUri, OAuthPkce.ChallengeS256(verifier), state));

        var query = await listener.WaitForCallbackAsync(
            parameters => RespondToCallback(parameters, state),
            ct).ConfigureAwait(false);
        var code = ValidateCallback(query, state);

        var tokens = await ExchangeCodeAsync(http, code, redirectUri, verifier, ct).ConfigureAwait(false);
        store.Save(CredentialName, tokens.ToStorageJson());
        return tokens.Email;
    }

    private static LoopbackHttpListener StartListener()
    {
        foreach (var port in Ports)
        {
            try
            {
                return new LoopbackHttpListener(port, CallbackPath);
            }
            catch (SocketException)
            {
                // Port busy (possibly a codex CLI login in flight); try the registered fallback.
            }
        }

        throw new InvalidOperationException(
            $"Ports {string.Join(" and ", Ports)} are in use; close other sign-in windows and retry.");
    }

    private static Uri BuildAuthorizeUri(string redirectUri, string challenge, string state)
    {
        var query = new StringBuilder()
            .Append("response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(ClientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&code_challenge_method=S256")
            .Append("&id_token_add_organizations=true")
            .Append("&codex_cli_simplified_flow=true")
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&originator=codex_cli_rs");
        return new Uri($"{Issuer}/oauth/authorize?{query}");
    }

    private static CallbackResult RespondToCallback(IReadOnlyDictionary<string, string> parameters, string state)
    {
        if (parameters.TryGetValue("error", out var error))
        {
            // A real provider error/denial ends the flow; ValidateCallback surfaces it.
            return CallbackResult.Done(LoopbackResponse.Html(400, SignInPages.Failure("Codex", parameters.GetValueOrDefault("error_description") ?? error)));
        }

        if (!parameters.TryGetValue("state", out var echoed) || echoed != state || !parameters.ContainsKey("code"))
        {
            // Stray/racing request (Codex uses fixed ports): answer it but keep waiting.
            return CallbackResult.KeepWaiting(LoopbackResponse.Html(400, SignInPages.Failure("Codex", "The sign-in response was invalid.")));
        }

        return CallbackResult.Done(LoopbackResponse.Html(200, SignInPages.Success("Codex")));
    }

    private static string ValidateCallback(IReadOnlyDictionary<string, string> parameters, string state)
    {
        if (parameters.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException($"Codex sign-in was rejected: {parameters.GetValueOrDefault("error_description") ?? error}.");
        }

        if (!parameters.TryGetValue("state", out var echoed) || echoed != state)
        {
            throw new InvalidOperationException("Codex sign-in state mismatch; aborting.");
        }

        return parameters.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code)
            ? code
            : throw new InvalidOperationException("Codex sign-in returned no authorization code.");
    }

    private static async Task<CodexTokenResult> ExchangeCodeAsync(
        HttpClient http,
        string code,
        string redirectUri,
        string verifier,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = ProviderHttpClient.TimeoutCts(ct, ExchangeTimeout);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Codex token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var idToken = GetString(root, "id_token");
        var accessToken = GetString(root, "access_token");
        var refreshToken = GetString(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(idToken))
        {
            throw new InvalidOperationException("Codex token exchange response was missing tokens.");
        }

        var claims = ParseJwtPayload(idToken);
        return new CodexTokenResult(idToken, accessToken, refreshToken, ReadAccountId(claims), ReadEmail(claims));
    }

    private static string? ReadAccountId(JsonElement payload)
    {
        // The CLI reads account_id from the id_token's https://api.openai.com/auth claim.
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("https://api.openai.com/auth", out var auth) &&
            auth.ValueKind == JsonValueKind.Object &&
            auth.TryGetProperty("chatgpt_account_id", out var accountId) &&
            accountId.ValueKind == JsonValueKind.String)
        {
            return accountId.GetString();
        }

        return null;
    }

    private static string? ReadEmail(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("email", out var email) &&
        email.ValueKind == JsonValueKind.String
            ? email.GetString()
            : null;

    private static JsonElement ParseJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return default;
        }

        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CodexTokenResult(
        string IdToken,
        string AccessToken,
        string RefreshToken,
        string? AccountId,
        string? Email)
    {
        /// <summary>Serializes to the document shape <see cref="CodexCredentialStore"/> parses.</summary>
        public string ToStorageJson() => new JsonObject
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = this.IdToken,
                ["access_token"] = this.AccessToken,
                ["refresh_token"] = this.RefreshToken,
                ["account_id"] = this.AccountId,
            },
            ["last_refresh"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
