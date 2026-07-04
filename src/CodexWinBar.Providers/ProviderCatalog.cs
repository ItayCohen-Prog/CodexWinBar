using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers;

/// <summary>Composition seam: all v1 provider descriptors. Owned by the orchestrator (amendment A12).</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderDescriptor> CreateAll() =>
    [
        Codex.CodexProvider.Create(),
        Claude.ClaudeProvider.Create(),
        OpenRouter.OpenRouterProvider.Create(),
        OpenAIAdmin.OpenAIAdminProvider.Create(),
        Copilot.CopilotProvider.Create(),
        Gemini.GeminiProvider.Create(),
        Zai.ZaiProvider.Create(),
    ];
}
