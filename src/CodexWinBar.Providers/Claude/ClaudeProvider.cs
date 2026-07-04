using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Claude;

/// <summary>Claude provider. STUB — replaced by implementation wave WB.</summary>
public static class ClaudeProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Claude,
        Metadata = new ProviderMetadata { DisplayName = "Claude", DefaultEnabled = true },
        Branding = new ProviderBranding { GlyphKey = "claude", R = 217, G = 119, B = 87 },
        Strategies = [],
    };
}
