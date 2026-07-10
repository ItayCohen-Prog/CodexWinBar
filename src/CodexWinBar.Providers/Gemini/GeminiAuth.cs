using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexWinBar.Core.Auth;
using CodexWinBar.Core.Http;

namespace CodexWinBar.Providers.Gemini;

/// <summary>
/// In-app OAuth sign-in for Gemini (Google Code Assist personal login), replicating the Gemini
/// CLI's installed-app flow on an ephemeral 127.0.0.1 port. Tokens are stored in the app's own
/// credential store; nothing is read from the CLI's files.
/// </summary>
public static class GeminiAuth
{
    /// <summary>Credential-store document name shared with <see cref="GeminiOAuthFetchStrategy"/>.</summary>
    public const string CredentialName = "gemini";

    private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string SuccessRedirect = "https://developers.google.com/gemini-code-assist/auth_success_gemini";
    private const string CallbackPath = "/oauth2callback";
    private const string Scope =
        "https://www.googleapis.com/auth/cloud-platform " +
        "https://www.googleapis.com/auth/userinfo.email " +
        "https://www.googleapis.com/auth/userinfo.profile";

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the browser sign-in: opens the authorize URL, waits for the loopback callback,
    /// exchanges the code, and saves the tokens. Returns the signed-in account email when known.
    /// </summary>
    public static async Task<string?> SignInAsync(
        HttpClient http,
        AppCredentialStore store,
        string userProfileDirectory,
        Action<Uri> openBrowser,
        CancellationToken ct)
    {
        var client = GeminiOAuthClientCredentials.Resolve(userProfileDirectory) ??
            throw new InvalidOperationException(
                "Gemini sign-in requires the Gemini CLI to be installed (its OAuth client is read from it).");

        using var listener = new LoopbackHttpListener(0, CallbackPath);
        var redirectUri = $"http://127.0.0.1:{listener.Port}{CallbackPath}";
        var verifier = OAuthPkce.CreateVerifier();
        var state = OAuthPkce.CreateState();
        openBrowser(BuildAuthorizeUri(client.ClientId, redirectUri, OAuthPkce.ChallengeS256(verifier), state));

        var query = await listener.WaitForCallbackAsync(
            parameters => RespondToCallback(parameters, state),
            ct).ConfigureAwait(false);
        var code = ValidateCallback(query, state);

        var credsJson = await ExchangeCodeAsync(http, client, code, redirectUri, verifier, ct).ConfigureAwait(false);
        var email = await TryFetchEmailAsync(http, credsJson, ct).ConfigureAwait(false);
        if (email is not null)
        {
            credsJson["email"] = email;
        }

        store.Save(CredentialName, credsJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return email;
    }

    private static Uri BuildAuthorizeUri(string clientId, string redirectUri, string challenge, string state)
    {
        // prompt=consent forces Google to issue a refresh token even when the user previously
        // granted this client (e.g. through the Gemini CLI); without it re-consents omit it.
        // PKCE (S256) binds the code to this app so a captured code cannot be redeemed without the
        // verifier, closing the ephemeral-port code-theft window despite the public client secret.
        var query = new StringBuilder()
            .Append("response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&access_type=offline")
            .Append("&prompt=consent")
            .Append("&code_challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&code_challenge_method=S256")
            .Append("&state=").Append(Uri.EscapeDataString(state));
        return new Uri($"{AuthorizeUrl}?{query}");
    }

    private static CallbackResult RespondToCallback(IReadOnlyDictionary<string, string> parameters, string state)
    {
        if (parameters.ContainsKey("error"))
        {
            // A real provider error/denial ends the flow; ValidateCallback surfaces it.
            return CallbackResult.Done(LoopbackResponse.Html(400, SignInPages.Failure("Gemini", parameters["error"])));
        }

        if (parameters.TryGetValue("state", out var echoed) && echoed == state && parameters.ContainsKey("code"))
        {
            return CallbackResult.Done(LoopbackResponse.Redirect(SuccessRedirect));
        }

        // Stray/racing request: answer it but keep waiting for the genuine callback.
        return CallbackResult.KeepWaiting(LoopbackResponse.Html(400, SignInPages.Failure("Gemini", "The sign-in response was invalid.")));
    }

    private static string ValidateCallback(IReadOnlyDictionary<string, string> parameters, string state)
    {
        if (parameters.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException($"Gemini sign-in was rejected: {error}.");
        }

        if (!parameters.TryGetValue("state", out var echoed) || echoed != state)
        {
            throw new InvalidOperationException("Gemini sign-in state mismatch; aborting.");
        }

        return parameters.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code)
            ? code
            : throw new InvalidOperationException("Gemini sign-in returned no authorization code.");
    }

    private static async Task<JsonObject> ExchangeCodeAsync(
        HttpClient http,
        OAuthClientCredentials client,
        string code,
        string redirectUri,
        string verifier,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["code_verifier"] = verifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = ProviderHttpClient.TimeoutCts(ct, ExchangeTimeout);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = GetString(root, "access_token");
        var refreshToken = GetString(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Gemini token exchange response was missing tokens.");
        }

        // Same document shape as the Gemini CLI's oauth_creds.json so the fetch strategy parses it.
        var creds = new JsonObject
        {
            ["access_token"] = accessToken,
            ["refresh_token"] = refreshToken,
            ["token_type"] = GetString(root, "token_type") ?? "Bearer",
        };
        if (GetString(root, "scope") is { } scope)
        {
            creds["scope"] = scope;
        }

        if (GetString(root, "id_token") is { } idToken)
        {
            creds["id_token"] = idToken;
        }

        if (root.TryGetProperty("expires_in", out var expires) && expires.TryGetDouble(out var seconds) && seconds > 0)
        {
            creds["expiry_date"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeMilliseconds();
        }

        return creds;
    }

    private static async Task<string?> TryFetchEmailAsync(HttpClient http, JsonObject creds, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds["access_token"]!.GetValue<string>());
            using var timeout = ProviderHttpClient.TimeoutCts(ct, ExchangeTimeout);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return GetString(document.RootElement, "email");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // The email is decorative identity info; sign-in must not fail because userinfo did.
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
