namespace CodexWinBar.Core.Providers;

/// <summary>Static, UI-facing facts about a provider — port of upstream ProviderMetadata.</summary>
public sealed record ProviderMetadata
{
    public required string DisplayName { get; init; }
    /// <summary>Label for the primary window, e.g. "5h" / "Session".</summary>
    public string SessionLabel { get; init; } = "5h";
    public string WeeklyLabel { get; init; } = "Weekly";
    public bool DefaultEnabled { get; init; }
    public bool SupportsCredits { get; init; }
    public Uri? DashboardUrl { get; init; }
    /// <summary>Statuspage.io base URL when the provider has one (drives status polling).</summary>
    public Uri? StatusPageUrl { get; init; }
    /// <summary>Google Workspace product id (Gemini) for the incidents feed; mutually exclusive with StatusPageUrl.</summary>
    public string? StatusWorkspaceProductId { get; init; }
}

/// <summary>Brand color + glyph key used by the widget and flyout.</summary>
public sealed record ProviderBranding
{
    /// <summary>Key into the app's glyph set (vector-drawn initials/logos).</summary>
    public required string GlyphKey { get; init; }
    public required byte R { get; init; }
    public required byte G { get; init; }
    public required byte B { get; init; }
}

/// <summary>Everything the engine needs to know about one provider.</summary>
public sealed class ProviderDescriptor
{
    public required ProviderId Id { get; init; }
    public required ProviderMetadata Metadata { get; init; }
    public required ProviderBranding Branding { get; init; }
    /// <summary>Ordered fetch strategies; see FetchPipeline for fallback semantics.</summary>
    public required IReadOnlyList<IFetchStrategy> Strategies { get; init; }
}
