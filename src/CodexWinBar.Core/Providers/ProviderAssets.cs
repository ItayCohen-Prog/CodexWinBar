using System.Collections.Concurrent;
using System.Reflection;

namespace CodexWinBar.Core.Providers;

/// <summary>
/// Access to the embedded provider brand logos (128px PNGs rasterized from upstream
/// steipete/CodexBar SVGs). Keyed by <see cref="ProviderBranding.GlyphKey"/>, which equals the
/// logo file basename (e.g. "codex" → Assets/logos/codex.png, with an optional "codex-dark.png"
/// variant used on dark backgrounds).
/// </summary>
public static class ProviderAssets
{
    private const string Prefix = "CodexWinBar.Core.Assets.logos.";
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<HashSet<string>> Names = new(() =>
        [.. Assembly.GetExecutingAssembly().GetManifestResourceNames()]);

    /// <summary>
    /// Returns the PNG bytes for a provider logo, preferring the "-dark" variant on dark
    /// backgrounds when one exists; null when no logo ships for the key.
    /// </summary>
    public static byte[]? GetLogoPng(string glyphKey, bool darkBackground)
    {
        if (string.IsNullOrWhiteSpace(glyphKey))
        {
            return null;
        }

        if (darkBackground)
        {
            var dark = Load($"{glyphKey}-dark");
            if (dark is not null)
            {
                return dark;
            }
        }

        return Load(glyphKey);
    }

    private static byte[]? Load(string baseName) => Cache.GetOrAdd(baseName, static key =>
    {
        var resource = Prefix + key + ".png";
        if (!Names.Value.Contains(resource))
        {
            return null;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });
}
