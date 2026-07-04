using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers.Copilot;

/// <summary>GitHub Copilot provider. STUB — replaced by implementation wave WB.</summary>
public static class CopilotProvider
{
    public static ProviderDescriptor Create() => new()
    {
        Id = ProviderId.Copilot,
        Metadata = new ProviderMetadata { DisplayName = "GitHub Copilot", DefaultEnabled = false },
        Branding = new ProviderBranding { GlyphKey = "copilot", R = 36, G = 41, B = 47 },
        Strategies = [],
    };
}
