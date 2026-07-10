using CodexWinBar.Core.Providers;

namespace CodexWinBar.Providers;

/// <summary>Composition seam: all v1 provider descriptors. Owned by the orchestrator (amendment A12).</summary>
public static class ProviderCatalog
{
    // Gemini is deferred: Google restricts third-party use of the Code Assist API (cloudcode-pa) and is
    // deprecating the individual tier, so it is intentionally omitted from the shipping catalog for now.
    // Its provider/auth code remains for a future revival via an installed gemini-cli.
    public static IReadOnlyList<ProviderDescriptor> CreateAll() =>
    [
        Codex.CodexProvider.Create(),
        Claude.ClaudeProvider.Create(),
        Copilot.CopilotProvider.Create(),
        OpenRouter.OpenRouterProvider.Create(),
        OpenAIAdmin.OpenAIAdminProvider.Create(),
        Zai.ZaiProvider.Create(),
        Cursor.CursorProvider.Create(),
    ];
}
