using CodexWinBar.App.Updates;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class AppUpdatePresentationsTests
{
    [Theory]
    [InlineData(AppUpdateStage.Idle, "Check now", true)]
    [InlineData(AppUpdateStage.Checking, "Checking…", false)]
    [InlineData(AppUpdateStage.UpToDate, "Check again", true)]
    [InlineData(AppUpdateStage.Available, "Download", true)]
    [InlineData(AppUpdateStage.Downloading, "Downloading 42%", false)]
    [InlineData(AppUpdateStage.Ready, "Restart & update", true)]
    [InlineData(AppUpdateStage.CheckError, "Try again", true)]
    [InlineData(AppUpdateStage.DownloadError, "Retry download", true)]
    internal void For_ReturnsExpectedAction(AppUpdateStage stage, string buttonLabel, bool enabled)
    {
        var presentation = AppUpdatePresentations.For(new AppUpdateStatus(stage, "1.2.3", 42, "Test error"));

        Assert.Equal(buttonLabel, presentation.ButtonLabel);
        Assert.Equal(enabled, presentation.ButtonEnabled);
    }

    [Fact]
    public void For_Available_IncludesTargetVersion()
    {
        var presentation = AppUpdatePresentations.For(new AppUpdateStatus(AppUpdateStage.Available, "1.2.3"));

        Assert.Equal("Version 1.2.3 is available.", presentation.Status);
    }

    [Fact]
    public void For_Error_ShowsFriendlyError()
    {
        var presentation = AppUpdatePresentations.For(new AppUpdateStatus(
            AppUpdateStage.DownloadError,
            "1.2.3",
            Error: "No internet connection"));

        Assert.Equal("No internet connection", presentation.Status);
    }
}
