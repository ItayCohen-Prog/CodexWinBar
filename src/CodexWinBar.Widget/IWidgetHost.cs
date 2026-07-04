namespace CodexWinBar.Widget;

/// <summary>Integration mode requested by settings; Auto probes embedded then falls back (amendment A13).</summary>
public enum WidgetMode
{
    Auto = 0,
    Embedded,
    Overlay,
    Hidden,
}

/// <summary>What one provider chip renders. Immutable snapshot marshaled to the widget thread.</summary>
public sealed record WidgetChipState
{
    public required string ProviderKey { get; init; }
    public required string GlyphKey { get; init; }
    public required byte BrandR { get; init; }
    public required byte BrandG { get; init; }
    public required byte BrandB { get; init; }
    /// <summary>0–100 fill for the top (session) gauge; null hides the gauge.</summary>
    public double? SessionPercent { get; init; }
    /// <summary>0–100 fill for the bottom (weekly) gauge; null hides the gauge.</summary>
    public double? WeeklyPercent { get; init; }
    /// <summary>Short text next to the gauges (e.g. "43%" or "2:13"); null for gauges-only.</summary>
    public string? Text { get; init; }
    /// <summary>Dim the chip (stale/error).</summary>
    public bool IsStale { get; init; }
    /// <summary>Incident dot color category: 0 none, 1 minor/maintenance, 2 major/critical/unknown.</summary>
    public int IncidentLevel { get; init; }
    public string? Tooltip { get; init; }
}

/// <summary>Full widget render state.</summary>
public sealed record WidgetRenderState
{
    public required IReadOnlyList<WidgetChipState> Chips { get; init; }
    public bool IsLoading { get; init; }
}

/// <summary>
/// Public surface of the taskbar widget (implemented in wave WA3). Owns a dedicated STA thread with its own
/// message pump (amendment A15); all members are safe to call from any thread.
/// </summary>
public interface IWidgetHost : IDisposable
{
    /// <summary>
    /// Creates the widget window per <paramref name="mode"/> and starts tracking the taskbar.
    /// <paramref name="anchorLeft"/> pins the widget to the taskbar's left edge instead of left-of-tray.
    /// </summary>
    void Start(WidgetMode mode, bool anchorLeft = false);

    /// <summary>Replaces the rendered state; cheap, coalesced on the widget thread.</summary>
    void Update(WidgetRenderState state);

    /// <summary>The mode currently in effect after probing/fallback (never Auto).</summary>
    WidgetMode EffectiveMode { get; }

    /// <summary>Current widget rect in physical screen pixels, or null when not placed (starting/hidden).</summary>
    System.Drawing.Rectangle? CurrentScreenRect { get; }

    /// <summary>Left-click on the widget. Arg = chip screen rect (physical pixels) to anchor the flyout.</summary>
    event Action<System.Drawing.Rectangle>? Clicked;

    /// <summary>Right-click on the widget (open context menu at cursor).</summary>
    event Action? RightClicked;

    /// <summary>Raised when effective mode changes (e.g. embed fell back to overlay), with a reason.</summary>
    event Action<WidgetMode, string>? ModeChanged;

    void Stop();
}
