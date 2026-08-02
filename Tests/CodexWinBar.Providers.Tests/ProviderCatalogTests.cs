using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Providers.Tests;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void Catalog_registers_default_disabled_Gemini_and_shipping_Cursor()
    {
        var providers = CodexWinBar.Providers.ProviderCatalog.CreateAll();

        var gemini = Assert.Single(providers, provider => provider.Id == ProviderId.Gemini);
        Assert.False(gemini.Metadata.DefaultEnabled);
        Assert.Single(providers, provider => provider.Id == ProviderId.Cursor);
    }

    [Fact]
    public void Claude_uses_Statuspage_base_url()
    {
        var claude = Assert.Single(
            CodexWinBar.Providers.ProviderCatalog.CreateAll(),
            provider => provider.Id == ProviderId.Claude);

        Assert.Equal("https://status.claude.com/", claude.Metadata.StatusPageUrl?.AbsoluteUri);
    }
}
