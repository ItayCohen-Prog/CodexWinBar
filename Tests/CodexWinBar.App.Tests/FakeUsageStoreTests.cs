using CodexWinBar.App.Dev;
using CodexWinBar.Providers;
using Xunit;

namespace CodexWinBar.App.Tests;

public sealed class FakeUsageStoreTests
{
    [Fact]
    public void States_OnlyContainShippingProviders()
    {
        using var store = new FakeUsageStore();
        var shippingProviders = ProviderCatalog.CreateAll().Select(item => item.Id).ToHashSet();

        Assert.All(store.States, state => Assert.Contains(state.Provider, shippingProviders));
        Assert.Equal(shippingProviders.Count, store.States.Count);
    }
}
