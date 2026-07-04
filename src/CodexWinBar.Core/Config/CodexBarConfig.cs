using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexWinBar.Core.Config;

/// <summary>
/// Upstream-compatible CodexBar config root.
/// </summary>
public sealed record CodexBarConfig
{
    /// <summary>
    /// Supported config schema version.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Provider configuration entries in file order.
    /// </summary>
    [JsonPropertyName("providers")]
    public IReadOnlyList<ProviderConfigEntry> Providers { get; init; } = [];

    /// <summary>
    /// Unknown root fields preserved across saves.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
