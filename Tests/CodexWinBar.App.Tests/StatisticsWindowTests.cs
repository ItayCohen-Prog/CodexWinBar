using System.Reflection;
using CodexWinBar.App.Dev;
using CodexWinBar.App.Statistics;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Statistics;
using CodexWinBar.Providers;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class StatisticsWindowTests
{
    [Fact]
    public void Refresh_can_rebuild_dashboard_repeatedly_without_visual_reparenting()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var store = new FakePlanStatisticsStore();
                var constructor = typeof(StatisticsWindow).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [typeof(IPlanStatisticsStore), typeof(IReadOnlyList<ProviderDescriptor>)],
                    modifiers: null);
                var window = Assert.IsType<StatisticsWindow>(constructor?.Invoke([store, ProviderCatalog.CreateAll()]));
                var refresh = typeof(StatisticsWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);

                refresh?.Invoke(window, null);
                refresh?.Invoke(window, null);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF dashboard refresh did not finish.");
        Assert.Null(failure);
    }
}
