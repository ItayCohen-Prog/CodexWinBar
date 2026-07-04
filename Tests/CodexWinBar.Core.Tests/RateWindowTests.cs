using CodexWinBar.Core.Models;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class RateWindowTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(37.5, 62.5)]
    [InlineData(100, 0)]
    [InlineData(125, 0)]
    public void RemainingPercent_is_derived_from_used_percent_and_never_negative(double used, double remaining)
    {
        var window = new RateWindow { UsedPercent = used };

        Assert.Equal(remaining, window.RemainingPercent);
    }
}
