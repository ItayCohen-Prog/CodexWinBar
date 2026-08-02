using CodexWinBar.App.Notifications;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class ResetWindowHistoryTests
{
    [Fact]
    public void Observe_ResetTransitionReportsPriorNotificationOnceAndPrunesEvents()
    {
        var history = new ResetWindowHistory<double>(256);
        var slot = new NotificationSlot(ProviderId.Codex, "session");

        _ = history.Observe(slot, "first", 0);
        Assert.True(history.TryMarkFired(slot, "first", "depleted", marksWindowNotified: true));
        Assert.True(history.TryMarkFired(slot, "first", "threshold:20"));

        var transition = history.Observe(slot, "second", 100);

        Assert.True(transition.ResetChanged);
        Assert.True(transition.PreviousWasNotified);
        Assert.False(transition.HadPrevious);
        Assert.Equal(0, history.FiredEventCount);

        var sameWindow = history.Observe(slot, "second", 99);
        Assert.False(sameWindow.ResetChanged);
        Assert.False(sameWindow.PreviousWasNotified);
        Assert.True(sameWindow.HadPrevious);
    }

    [Fact]
    public void ClearWindowNotification_PreventsResetFromReportingAlreadyRestoredWindow()
    {
        var history = new ResetWindowHistory<double>(256);
        var slot = new NotificationSlot(ProviderId.Claude, "weekly");

        _ = history.Observe(slot, "first", 0);
        _ = history.TryMarkFired(slot, "first", "depleted", marksWindowNotified: true);
        history.ClearWindowNotification(slot, "first");

        var transition = history.Observe(slot, "second", 100);

        Assert.True(transition.ResetChanged);
        Assert.False(transition.PreviousWasNotified);
    }

    [Fact]
    public void RetainOnly_PrunesInactiveWindowsAndTheirEvents()
    {
        var history = new ResetWindowHistory<int>(256);
        var keep = new NotificationSlot(ProviderId.Codex, "session");
        var remove = new NotificationSlot(ProviderId.Claude, "weekly");
        _ = history.Observe(keep, "a", 1);
        _ = history.Observe(remove, "b", 2);
        _ = history.TryMarkFired(remove, "b", "underuse");

        history.RetainOnly(new HashSet<NotificationSlot> { keep });

        Assert.Equal(1, history.ActiveWindowCount);
        Assert.Equal(0, history.FiredEventCount);
    }
}
