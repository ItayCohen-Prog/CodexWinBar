using System.Text.Json.Serialization;

namespace CodexWinBar.Core.Config;

/// <summary>
/// Display text choices for the widget and flyout.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayTextMode>))]
public enum DisplayTextMode
{
    /// <summary>
    /// Show remaining percentage.
    /// </summary>
    [JsonStringEnumMemberName("percent")]
    Percent,

    /// <summary>
    /// Show usage pace.
    /// </summary>
    [JsonStringEnumMemberName("pace")]
    Pace,

    /// <summary>
    /// Show percentage and pace.
    /// </summary>
    [JsonStringEnumMemberName("both")]
    Both,

    /// <summary>
    /// Show reset time.
    /// </summary>
    [JsonStringEnumMemberName("resetTime")]
    ResetTime,
}

/// <summary>
/// Widget hosting mode.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WidgetMode>))]
public enum WidgetMode
{
    /// <summary>
    /// Try embedded mode and fall back to overlay when required.
    /// </summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>
    /// Embed in the Windows taskbar.
    /// </summary>
    [JsonStringEnumMemberName("embedded")]
    Embedded,

    /// <summary>
    /// Use an overlay near the taskbar.
    /// </summary>
    [JsonStringEnumMemberName("overlay")]
    Overlay,

    /// <summary>
    /// Hide the widget.
    /// </summary>
    [JsonStringEnumMemberName("hidden")]
    Hidden,
}

/// <summary>
/// Widget side preference.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WidgetSide>))]
public enum WidgetSide
{
    /// <summary>
    /// Place the widget on the right.
    /// </summary>
    [JsonStringEnumMemberName("right")]
    Right,

    /// <summary>
    /// Place the widget on the left.
    /// </summary>
    [JsonStringEnumMemberName("left")]
    Left,
}

/// <summary>
/// Windows-only UI preferences stored outside upstream config.json.
/// </summary>
public sealed class UiSettings
{
    /// <summary>
    /// Refresh cadence in minutes; null represents manual refresh.
    /// </summary>
    [JsonPropertyName("refreshCadence")]
    public int? RefreshCadenceMinutes { get; set; } = 5;

    /// <summary>
    /// Whether to merge provider icons in the taskbar widget.
    /// TODO: not yet wired to any UI or widget behavior; kept so the setting round-trips.
    /// </summary>
    [JsonPropertyName("mergeIcons")]
    public bool MergeIcons { get; set; } = true;

    /// <summary>
    /// Text mode used for provider usage.
    /// </summary>
    [JsonPropertyName("displayTextMode")]
    public DisplayTextMode DisplayTextMode { get; set; } = DisplayTextMode.Percent;

    /// <summary>
    /// Whether usage bars show used quota instead of remaining quota.
    /// </summary>
    [JsonPropertyName("usageBarsShowUsed")]
    public bool UsageBarsShowUsed { get; set; }

    /// <summary>
    /// Whether reset times are shown as absolute times.
    /// </summary>
    [JsonPropertyName("resetTimesShowAbsolute")]
    public bool ResetTimesShowAbsolute { get; set; }

    /// <summary>
    /// Whether to show the pace indicator (projected end-of-window usage) in the flyout and expanded widget.
    /// </summary>
    [JsonPropertyName("showPaceIndicator")]
    public bool ShowPaceIndicator { get; set; }

    /// <summary>
    /// Whether CodexWinBar launches at login.
    /// </summary>
    [JsonPropertyName("launchAtLogin")]
    public bool LaunchAtLogin { get; set; }

    /// <summary>
    /// Whether provider status checks are enabled.
    /// </summary>
    [JsonPropertyName("statusChecksEnabled")]
    public bool StatusChecksEnabled { get; set; } = true;

    /// <summary>
    /// Whether quota notifications are enabled.
    /// </summary>
    [JsonPropertyName("quotaNotificationsEnabled")]
    public bool QuotaNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Default remaining-quota thresholds for session window notifications when a provider has no override.
    /// </summary>
    [JsonPropertyName("quotaSessionThresholds")]
    public IReadOnlyList<int> QuotaSessionThresholds { get; set; } = [20];

    /// <summary>
    /// Whether default session window quota threshold notifications are enabled.
    /// </summary>
    [JsonPropertyName("quotaSessionEnabled")]
    public bool QuotaSessionEnabled { get; set; } = true;

    /// <summary>
    /// Default remaining-quota thresholds for weekly window notifications when a provider has no override.
    /// </summary>
    [JsonPropertyName("quotaWeeklyThresholds")]
    public IReadOnlyList<int> QuotaWeeklyThresholds { get; set; } = [20];

    /// <summary>
    /// Whether default weekly window quota threshold notifications are enabled.
    /// </summary>
    [JsonPropertyName("quotaWeeklyEnabled")]
    public bool QuotaWeeklyEnabled { get; set; } = true;

    /// <summary>
    /// Whether pace notifications are enabled (toast when a window's projected end-of-window usage
    /// crosses into the at-risk band). Opt-in; independent of quota threshold notifications.
    /// </summary>
    [JsonPropertyName("paceNotificationsEnabled")]
    public bool PaceNotificationsEnabled { get; set; }

    /// <summary>
    /// Whether pace notifications also fire when a window enters the under-using band (leaving a
    /// meaningful chunk of quota unused). Only consulted when <see cref="PaceNotificationsEnabled"/> is on.
    /// </summary>
    [JsonPropertyName("paceUnderuseNotificationsEnabled")]
    public bool PaceUnderuseNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Widget hosting mode.
    /// </summary>
    [JsonPropertyName("widgetMode")]
    public WidgetMode WidgetMode { get; set; } = WidgetMode.Auto;

    /// <summary>
    /// Widget side preference.
    /// </summary>
    [JsonPropertyName("widgetSide")]
    public WidgetSide WidgetSide { get; set; } = WidgetSide.Right;

    /// <summary>
    /// Repairs values that persisted JSON cannot be trusted to keep valid: the refresh cadence
    /// must be positive (or null for manual refresh), quota threshold lists must be non-null with
    /// entries clamped to 0-99, and enum values must be defined.
    /// </summary>
    /// <returns>This instance, for call chaining.</returns>
    public UiSettings Normalize()
    {
        if (this.RefreshCadenceMinutes is <= 0)
        {
            this.RefreshCadenceMinutes = 5;
        }

        this.QuotaSessionThresholds = NormalizeThresholds(this.QuotaSessionThresholds);
        this.QuotaWeeklyThresholds = NormalizeThresholds(this.QuotaWeeklyThresholds);

        if (!Enum.IsDefined(this.DisplayTextMode))
        {
            this.DisplayTextMode = DisplayTextMode.Percent;
        }

        if (!Enum.IsDefined(this.WidgetMode))
        {
            this.WidgetMode = WidgetMode.Auto;
        }

        if (!Enum.IsDefined(this.WidgetSide))
        {
            this.WidgetSide = WidgetSide.Right;
        }

        return this;
    }

    private static IReadOnlyList<int> NormalizeThresholds(IReadOnlyList<int>? thresholds)
    {
        if (thresholds is null)
        {
            return [20];
        }

        return thresholds
            .Select(threshold => Math.Clamp(threshold, 0, 99))
            .Distinct()
            .OrderDescending()
            .ToArray();
    }
}
