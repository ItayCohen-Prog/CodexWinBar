using System.IO;
using System.Windows.Media.Imaging;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.App.Assets;

internal static class LogoImages
{
    private static readonly Dictionary<(string GlyphKey, bool Dark), BitmapImage> Cache = [];

    internal static BitmapImage? Get(string glyphKey, bool darkBackground)
    {
        if (string.IsNullOrWhiteSpace(glyphKey))
        {
            return null;
        }

        var key = (glyphKey, darkBackground);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bytes = ProviderAssets.GetLogoPng(glyphKey, darkBackground);
        if (bytes is null)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        Cache[key] = image;
        return image;
    }
}
