using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexWinBar.Core.Config;

/// <summary>
/// One entry of the upstream-compatible config.json `providers` array. Unknown members are captured in
/// <see cref="Extra"/> and MUST round-trip on save so macOS-written fields survive.
/// </summary>
public sealed record ProviderConfigEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>auto | web | cli | oauth | api.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("cookieHeader")]
    public string? CookieHeader { get; init; }

    [JsonPropertyName("cookieSource")]
    public string? CookieSource { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("workspaceID")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("enterpriseHost")]
    public string? EnterpriseHost { get; init; }

    [JsonPropertyName("quotaWarnings")]
    public QuotaWarnings? QuotaWarnings { get; init; }

    [JsonPropertyName("extrasEnabled")]
    public bool? ExtrasEnabled { get; init; }

    [JsonPropertyName("secretKey")]
    public string? SecretKey { get; init; }

    /// <summary>Upstream multi-account store; opaque to the Windows port but must round-trip.</summary>
    [JsonPropertyName("tokenAccounts")]
    public JsonElement? TokenAccounts { get; init; }

    /// <summary>Upstream Codex managed-account selector; opaque, round-trip only.</summary>
    [JsonPropertyName("codexActiveSource")]
    public JsonElement? CodexActiveSource { get; init; }

    [JsonPropertyName("codexProfileHomePaths")]
    public JsonElement? CodexProfileHomePaths { get; init; }

    [JsonPropertyName("kiloKnownOrganizations")]
    public JsonElement? KiloKnownOrganizations { get; init; }

    [JsonPropertyName("kiloEnabledOrganizationIDs")]
    public JsonElement? KiloEnabledOrganizationIds { get; init; }

    [JsonPropertyName("awsProfile")]
    public string? AwsProfile { get; init; }

    [JsonPropertyName("awsAuthMode")]
    public string? AwsAuthMode { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Threshold notification settings; upstream defaults are [50, 20], range 0–99.</summary>
public sealed record QuotaWarnings
{
    [JsonPropertyName("session")]
    public QuotaWarningWindow? Session { get; init; }

    [JsonPropertyName("weekly")]
    public QuotaWarningWindow? Weekly { get; init; }
}

public sealed record QuotaWarningWindow
{
    [JsonPropertyName("thresholds")]
    public IReadOnlyList<int>? Thresholds { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonIgnore]
    public static IReadOnlyList<int> DefaultThresholds { get; } = [50, 20];
}
