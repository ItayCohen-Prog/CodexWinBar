namespace CodexWinBar.Core.Providers;

/// <summary>Compile-time provider identifiers (v1 set).</summary>
public enum ProviderId
{
    Codex,
    Claude,
    OpenRouter,
    /// <summary>OpenAI Admin-API org usage — upstream config id "openai".</summary>
    OpenAIAdmin,
    Copilot,
    Gemini,
    Zai,
    Cursor,
}

/// <summary>Mapping between enum values and the upstream config-file string ids.</summary>
public static class ProviderIds
{
    private static readonly Dictionary<ProviderId, string> ToConfig = new()
    {
        [ProviderId.Codex] = "codex",
        [ProviderId.Claude] = "claude",
        [ProviderId.OpenRouter] = "openrouter",
        [ProviderId.OpenAIAdmin] = "openai",
        [ProviderId.Copilot] = "copilot",
        [ProviderId.Gemini] = "gemini",
        [ProviderId.Zai] = "zai",
        [ProviderId.Cursor] = "cursor",
    };

    private static readonly Dictionary<string, ProviderId> FromConfig =
        ToConfig.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The upstream CodexBar config.json id (lowercase) for a provider.</summary>
    public static string ConfigId(this ProviderId id) => ToConfig[id];

    /// <summary>Resolves an upstream config id; null for providers this port does not implement (they must be preserved verbatim in the file).</summary>
    public static ProviderId? TryParse(string configId) =>
        FromConfig.TryGetValue(configId, out var id) ? id : null;

    public static IReadOnlyList<ProviderId> All { get; } = [.. ToConfig.Keys];
}
