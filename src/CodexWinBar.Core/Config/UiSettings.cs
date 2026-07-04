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
    /// Widget hosting mode.
    /// </summary>
    [JsonPropertyName("widgetMode")]
    public WidgetMode WidgetMode { get; set; } = WidgetMode.Auto;

    /// <summary>
    /// Widget side preference.
    /// </summary>
    [JsonPropertyName("widgetSide")]
    public WidgetSide WidgetSide { get; set; } = WidgetSide.Right;
}
