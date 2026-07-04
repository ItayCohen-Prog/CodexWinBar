using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Zai;

/// <summary>z.ai provider. STUB — replaced by implementation wave WB.</summary>
public static class ZaiProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Zai,
        Metadata = new ProviderMetadata { DisplayName = "z.ai", DefaultEnabled = false },
        Branding = new ProviderBranding { GlyphKey = "zai", R = 205, G = 62, B = 253 },
        Strategies = [],
    };
}
