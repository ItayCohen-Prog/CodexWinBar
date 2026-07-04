using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Codex;

/// <summary>Codex provider. STUB — replaced by implementation wave WB.</summary>
public static class CodexProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Codex,
        Metadata = new ProviderMetadata { DisplayName = "Codex", DefaultEnabled = true },
        Branding = new ProviderBranding { GlyphKey = "codex", R = 30, G = 63, B = 69 },
        Strategies = [],
    };
}
