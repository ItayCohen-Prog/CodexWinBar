using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.OpenRouter;

/// <summary>OpenRouter provider. STUB — replaced by implementation wave WB.</summary>
public static class OpenRouterProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.OpenRouter,
        Metadata = new ProviderMetadata { DisplayName = "OpenRouter", DefaultEnabled = false },
        Branding = new ProviderBranding { GlyphKey = "openrouter", R = 101, G = 82, B = 255 },
        Strategies = [],
    };
}
