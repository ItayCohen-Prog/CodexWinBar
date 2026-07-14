namespace CodexWinBar.App.Updates;

internal enum AppUpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    CheckError,
    DownloadError,
}

internal sealed record AppUpdateStatus(
    AppUpdateStage Stage,
    string? Version = null,
    double Progress = 0,
    string? Error = null);

internal sealed record AppUpdatePresentation(string Status, string ButtonLabel, bool ButtonEnabled);

internal static class AppUpdatePresentations
{
    internal static AppUpdatePresentation For(AppUpdateStatus state) => state.Stage switch
    {
        AppUpdateStage.Checking => new("Checking GitHub for updates…", "Checking…", false),
        AppUpdateStage.UpToDate => new(
            state.Version is null ? "You’re up to date." : $"Version {state.Version} is up to date.",
            "Check again",
            true),
        AppUpdateStage.Available => new(
            state.Version is null ? "An update is available." : $"Version {state.Version} is available.",
            "Download",
            true),
        AppUpdateStage.Downloading => new(
            $"Downloading update… {state.Progress:0}%",
            $"Downloading {state.Progress:0}%",
            false),
        AppUpdateStage.Ready => new(
            state.Version is null ? "Update ready to install." : $"Version {state.Version} is ready.",
            "Restart & update",
            true),
        AppUpdateStage.CheckError => new(state.Error ?? "Couldn’t check for updates.", "Try again", true),
        AppUpdateStage.DownloadError => new(state.Error ?? "Couldn’t download the update.", "Retry download", true),
        _ => new("Check for a newer CodexWinBar release.", "Check now", true),
    };
}
