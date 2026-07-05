namespace CodexWinBar.Core.Models;

/// <summary>Where a window's projected end-of-window usage lands relative to a healthy pace.</summary>
public enum PaceState
{
    /// <summary>On track to finish the window having used only a little of the quota — money left on the table.</summary>
    Underusing,

    /// <summary>On track to use most of the quota without running out — the healthy zone.</summary>
    OnTrack,

    /// <summary>On pace to hit 100% before the window resets — you'll get cut off early.</summary>
    AtRisk,
}

/// <summary>
/// Projected end-of-window usage for a rate-limit window, assuming the current burn rate holds.
/// </summary>
/// <param name="ProjectedPercent">
/// Usage the window is on track to reach by its reset: <c>used% ÷ (elapsed time ÷ window length)</c>.
/// </param>
/// <param name="State">Which pace band the projection falls into.</param>
public readonly record struct UsagePace(double ProjectedPercent, PaceState State)
{
    /// <summary>Convenience flag for the at-risk band (on pace to run out before reset).</summary>
    public bool AtRisk => this.State == PaceState.AtRisk;
}

/// <summary>
/// Computes usage "pace": are you burning a quota faster than the window refills it, keeping up, or
/// barely touching it? The projection is a straight-line extrapolation of current usage to the reset.
/// </summary>
public static class PaceCalculator
{
    /// <summary>At or above this projected usage you are on pace to run out before the window resets.</summary>
    public const double AtRiskThreshold = 100;

    /// <summary>Below this projected usage you are leaving a meaningful chunk of the quota unused.</summary>
    public const double UnderusingThreshold = 60;

    /// <summary>
    /// Ignore the first slice of a window: right after a reset a tiny elapsed fraction makes the
    /// straight-line projection wildly unstable (1% used in the first 0.5% of time → "200%").
    /// </summary>
    private const double MinElapsedFraction = 0.05;

    /// <summary>Cap the reported projection so the UI never shows an absurd number.</summary>
    private const double MaxProjectedPercent = 999;

    /// <summary>
    /// Returns the pace for a window, or null when it cannot be computed meaningfully (no reset time,
    /// no known window length, the window has already elapsed, or too little of it has passed yet).
    /// </summary>
    public static UsagePace? Compute(RateWindow window, DateTimeOffset now)
    {
        if (window.WindowMinutes is not { } minutes || minutes <= 0 || window.ResetsAt is not { } resetsAt)
        {
            return null;
        }

        var remainingMinutes = (resetsAt - now).TotalMinutes;
        var elapsedFraction = 1.0 - (remainingMinutes / minutes);
        if (elapsedFraction < MinElapsedFraction || elapsedFraction > 1.0)
        {
            return null;
        }

        var projected = window.UsedPercent / elapsedFraction;
        return new UsagePace(Math.Min(projected, MaxProjectedPercent), Classify(projected));
    }

    private static PaceState Classify(double projected) => projected switch
    {
        >= AtRiskThreshold => PaceState.AtRisk,
        < UnderusingThreshold => PaceState.Underusing,
        _ => PaceState.OnTrack,
    };
}
