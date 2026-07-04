namespace CodexWinBar.Core.Models;

/// <summary>A single rate-limit window (session, weekly, …) normalized across providers.</summary>
public sealed record RateWindow
{
    /// <summary>Percent of the window consumed, clamped 0–100.</summary>
    public required double UsedPercent { get; init; }

    /// <summary>Window length in minutes when known (300 = 5h session, 10080 = weekly).</summary>
    public int? WindowMinutes { get; init; }

    /// <summary>Absolute reset instant when the provider reports one.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Provider-supplied reset text used verbatim when no instant is available.</summary>
    public string? ResetDescription { get; init; }

    /// <summary>For regenerating quotas: percent that will be available after the next regen tick.</summary>
    public double? NextRegenPercent { get; init; }

    /// <summary>True when the window is a synthesized placeholder rather than provider-reported data.</summary>
    public bool IsSyntheticPlaceholder { get; init; }

    /// <summary>Percent remaining, derived.</summary>
    public double RemainingPercent => Math.Max(0, 100 - this.UsedPercent);
}

/// <summary>An additional, provider-specific window surfaced beyond primary/secondary/tertiary.</summary>
public sealed record NamedRateWindow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required RateWindow Window { get; init; }
    /// <summary>False when the provider reports the window exists but utilization is unknown.</summary>
    public bool UsageKnown { get; init; } = true;
}
