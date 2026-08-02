using CodexWinBar.Widget;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class WidgetWindowLifecycleTests
{
    [Theory]
    [InlineData((ushort)1, 5, true)]
    [InlineData((ushort)0, NativeMethods.ERROR_CLASS_ALREADY_EXISTS, true)]
    [InlineData((ushort)0, 5, false)]
    public void IsClassRegistrationAccepted_HandlesExistingClassAsSuccess(ushort atom, int error, bool expected)
    {
        Assert.Equal(expected, WidgetWindow.IsClassRegistrationAccepted(atom, error));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ShouldLogOverlayDecision_LogsFirstDecisionAndChangesOnly(
        bool alreadyLogged,
        bool previousHide,
        bool hide,
        bool expected)
    {
        Assert.Equal(expected, WidgetWindow.ShouldLogOverlayDecision(alreadyLogged, previousHide, hide));
    }
}
