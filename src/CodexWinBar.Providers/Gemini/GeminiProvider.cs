using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Gemini;

/// <summary>Gemini provider. STUB — replaced by implementation wave WB.</summary>
public static class GeminiProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Gemini,
        Metadata = new ProviderMetadata { DisplayName = "Gemini", DefaultEnabled = false },
        Branding = new ProviderBranding { GlyphKey = "gemini", R = 66, G = 133, B = 244 },
        Strategies = [],
    };
}
