using System.Text.Json;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Providers;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void ResolvePath_prefers_CODEXBAR_CONFIG_override()
    {
        using var temp = TempDir.Create();
        var overridePath = Path.Combine(temp.Path, "override.json");
        var store = Store(temp.Path, name => name == "CODEXBAR_CONFIG" ? overridePath : null);

        Assert.Equal(overridePath, store.ResolvePath());
    }

    [Fact]
    public void ResolvePath_uses_fully_qualified_XDG_CONFIG_HOME()
    {
        using var temp = TempDir.Create();
        var xdg = Path.Combine(temp.Path, "xdg");
        var store = Store(temp.Path, name => name == "XDG_CONFIG_HOME" ? xdg : null);

        Assert.Equal(Path.Combine(xdg, "codexbar", "config.json"), store.ResolvePath());
    }

    [Fact]
    public void ResolvePath_ignores_relative_XDG_and_uses_existing_current_config()
    {
        using var temp = TempDir.Create();
        var current = Path.Combine(temp.Path, ".config", "codexbar", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "{}");
        var store = Store(temp.Path, name => name == "XDG_CONFIG_HOME" ? "relative-config" : null);

        Assert.Equal(current, store.ResolvePath());
    }

    [Fact]
    public void ResolvePath_uses_legacy_fallback_when_current_missing()
    {
        using var temp = TempDir.Create();
        var legacy = Path.Combine(temp.Path, ".codexbar", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{}");
        var store = Store(temp.Path);

        Assert.Equal(legacy, store.ResolvePath());
    }

    [Fact]
    public void Save_round_trips_unknown_root_provider_fields_unknown_entries_order_and_token_accounts()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Path, "config.json");
        var store = Store(temp.Path, name => name == "CODEXBAR_CONFIG" ? path : null);
        File.WriteAllText(path, """
            {
              "version": 1,
              "rootFuture": {"keep": true},
              "providers": [
                {"id": "codex", "enabled": true, "tokenAccounts": [{"id":"a","token":"secret"}], "futureProviderField": 42},
                {"id": "future-ai", "enabled": false, "nested": {"x": 1}},
                {"id": "claude", "source": "oauth"}
              ]
            }
            """);

        var loaded = store.Load();
        var patched = store.WithEntry(loaded, store.EntryFor(loaded, ProviderId.Claude) with { Enabled = true });
        store.Save(patched);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("rootFuture").GetProperty("keep").GetBoolean());
        var providers = root.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(["codex", "future-ai", "claude"], providers.Select(p => p.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(42, providers[0].GetProperty("futureProviderField").GetInt32());
        Assert.Equal("a", providers[0].GetProperty("tokenAccounts")[0].GetProperty("id").GetString());
        Assert.Equal(1, providers[1].GetProperty("nested").GetProperty("x").GetInt32());
        Assert.True(providers[2].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Normalize_clamps_deduplicates_sorts_descending_and_defaults_empty()
    {
        var normalized = ConfigStore.Normalize(new QuotaWarningWindow
        {
            Enabled = false,
            Thresholds = [20, 101, -4, 20, 99, 0],
        });

        Assert.Equal([99, 20, 0], normalized.Thresholds);
        Assert.False(normalized.Enabled);
        Assert.Equal([50, 20], ConfigStore.Normalize(new QuotaWarningWindow { Thresholds = [] }).Thresholds);
        Assert.Equal([50, 20], ConfigStore.Normalize(null).Thresholds);
    }

    private static ConfigStore Store(string userProfile, Func<string, string?>? env = null) =>
        new(env ?? (_ => null), userProfile, _ => { });
}
