using System.Net.Http.Headers;
using System.Text.Json;
using CodexWinBar.Core.Http;

namespace CodexWinBar.Providers.Copilot;

/// <summary>GitHub Copilot OAuth device-flow helper used by provider settings UI.</summary>
public static class CopilotAuth
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string Scope = "read:user";
    private static readonly Uri DeviceCodeUri = new("https://github.com/login/device/code");
    private static readonly Uri AccessTokenUri = new("https://github.com/login/oauth/access_token");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Starts the GitHub OAuth device flow and returns the user-facing device code details.</summary>
    public static async Task<DeviceCodeInfo> StartAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = ProviderHttpClient.TimeoutCts(ct, Timeout);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Copilot device-code request failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var expiresIn = GetInt(root, "expires_in") ?? 900;
        return new DeviceCodeInfo
        {
            UserCode = GetString(root, "user_code") ?? throw new InvalidOperationException("Copilot device-code response omitted user_code."),
            VerificationUri = GetString(root, "verification_uri") ?? throw new InvalidOperationException("Copilot device-code response omitted verification_uri."),
            DeviceCode = GetString(root, "device_code") ?? throw new InvalidOperationException("Copilot device-code response omitted device_code."),
            IntervalSeconds = GetInt(root, "interval") ?? 5,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
        };
    }

    /// <summary>Polls GitHub until the user authorizes, the code expires, or authorization is denied.</summary>
    public static async Task<string?> PollAsync(HttpClient http, DeviceCodeInfo info, CancellationToken ct)
    {
        var interval = Math.Max(1, info.IntervalSeconds);

        while (DateTimeOffset.UtcNow < info.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = info.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeout = ProviderHttpClient.TimeoutCts(ct, Timeout);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Copilot token poll failed with HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var accessToken = GetString(root, "access_token");
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                return accessToken;
            }

            switch (GetString(root, "error"))
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += 5;
                    break;
                case "expired_token" or "access_denied":
                    return null;
                case { } error:
                    throw new InvalidOperationException($"Copilot token poll failed: {error}.");
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}

/// <summary>GitHub OAuth device-code details shown by the settings UI.</summary>
public sealed record DeviceCodeInfo
{
    /// <summary>Short code the user enters on GitHub.</summary>
    public required string UserCode { get; init; }

    /// <summary>Verification URL where the user enters <see cref="UserCode"/>.</summary>
    public required string VerificationUri { get; init; }

    /// <summary>Opaque device code used by polling; never display it as a credential.</summary>
    public required string DeviceCode { get; init; }

    /// <summary>Server-provided polling interval in seconds.</summary>
    public required int IntervalSeconds { get; init; }

    /// <summary>UTC expiry instant for this device code.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
