using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.OpenAIAdmin;

/// <summary>OpenAI provider. STUB — replaced by implementation wave WB.</summary>
public static class OpenAIAdminProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.OpenAIAdmin,
        Metadata = new ProviderMetadata { DisplayName = "OpenAI", DefaultEnabled = false },
        Branding = new ProviderBranding { GlyphKey = "openaiadmin", R = 16, G = 163, B = 127 },
        Strategies = [],
    };
}
