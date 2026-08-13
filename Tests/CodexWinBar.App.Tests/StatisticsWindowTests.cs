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
                var selectDate = typeof(StatisticsWindow).GetMethod("SelectDate", BindingFlags.Instance | BindingFlags.NonPublic);
                var selectMonth = typeof(StatisticsWindow).GetMethod("SelectMonth", BindingFlags.Instance | BindingFlags.NonPublic);
                var selectWeek = typeof(StatisticsWindow).GetMethod("SelectWeek", BindingFlags.Instance | BindingFlags.NonPublic);
                var goBack = typeof(StatisticsWindow).GetMethod("GoBack", BindingFlags.Instance | BindingFlags.NonPublic);
                var scaleMode = typeof(StatisticsWindow).GetField("scaleMode", BindingFlags.Instance | BindingFlags.NonPublic);
                var viewMode = typeof(StatisticsWindow).GetField("viewMode", BindingFlags.Instance | BindingFlags.NonPublic);

                refresh?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Overview, viewMode?.GetValue(window));
                selectMonth?.Invoke(window, [DateOnly.FromDateTime(DateTime.Today)]);
                Assert.Equal(ActivityViewMode.Month, viewMode?.GetValue(window));
                selectWeek?.Invoke(window, [PlanStatisticsProjection.WeekStart(DateOnly.FromDateTime(DateTime.Today))]);
                Assert.Equal(ActivityViewMode.Week, viewMode?.GetValue(window));
                selectDate?.Invoke(window, [DateOnly.FromDateTime(DateTime.Today.AddDays(-2))]);
                Assert.Equal(ActivityViewMode.Day, viewMode?.GetValue(window));
                goBack?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Week, viewMode?.GetValue(window));
                goBack?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Month, viewMode?.GetValue(window));
                goBack?.Invoke(window, null);
                Assert.Equal(ActivityViewMode.Overview, viewMode?.GetValue(window));
                scaleMode?.SetValue(window, ActivityScaleMode.Fixed);
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
