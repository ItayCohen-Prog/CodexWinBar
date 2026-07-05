using CodexWinBar.Core.Models;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class PaceCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    // A 300-minute window that resets `remaining` minutes from now, `used`% consumed.
    private static RateWindow Window(double used, int remainingMinutes, int windowMinutes = 300) => new()
    {
        UsedPercent = used,
        WindowMinutes = windowMinutes,
        ResetsAt = Now.AddMinutes(remainingMinutes),
    };

    [Fact]
    public void AtRisk_when_projected_reaches_100_before_reset()
    {
        // Half the window elapsed (150 of 300), 70% already used -> projected 140%.
        var pace = PaceCalculator.Compute(Window(used: 70, remainingMinutes: 150), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceState.AtRisk, pace!.Value.State);
        Assert.True(pace.Value.AtRisk);
        Assert.Equal(140, pace.Value.ProjectedPercent, precision: 0);
    }

    [Fact]
    public void OnTrack_when_projection_lands_in_healthy_band()
    {
        // 60% elapsed (reset in 120 of 300), 45% used -> projected 75%.
        var pace = PaceCalculator.Compute(Window(used: 45, remainingMinutes: 120), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceState.OnTrack, pace!.Value.State);
        Assert.False(pace.Value.AtRisk);
    }

    [Fact]
    public void Underusing_when_projection_stays_well_below_full()
    {
        // 75% elapsed (reset in 75 of 300), 15% used -> projected 20%.
        var pace = PaceCalculator.Compute(Window(used: 15, remainingMinutes: 75), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceState.Underusing, pace!.Value.State);
    }

    [Fact]
    public void Null_when_too_little_of_the_window_has_elapsed()
    {
        // Only ~1.7% elapsed (reset in 295 of 300) — projection would be noise.
        Assert.Null(PaceCalculator.Compute(Window(used: 5, remainingMinutes: 295), Now));
    }

    [Fact]
    public void Null_when_window_length_or_reset_is_unknown()
    {
        Assert.Null(PaceCalculator.Compute(new RateWindow { UsedPercent = 50, ResetsAt = Now.AddHours(1) }, Now));
        Assert.Null(PaceCalculator.Compute(new RateWindow { UsedPercent = 50, WindowMinutes = 300 }, Now));
    }

    [Fact]
    public void Null_when_window_already_elapsed()
    {
        Assert.Null(PaceCalculator.Compute(Window(used: 50, remainingMinutes: -10), Now));
    }
}
