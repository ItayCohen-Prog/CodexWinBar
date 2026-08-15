using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.App.Assets;

internal static class LogoImages
{
    internal const string RefreshGlyph = "\uE72C";
    internal const string StatisticsGlyph = "\uE9D2";
    internal const string SettingsGlyph = "\uE713";
    internal const string CloseGlyph = "\uE711";
    internal const string CopyGlyph = "\uE8C8";
    internal const string SignInGlyph = "\uE77B";
    internal const string WarningGlyph = "\uE7BA";
    internal const string ExternalLinkGlyph = "\uE8A7";
    internal const string DownloadGlyph = "\uE896";

    private static readonly FontFamily IconFontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Dictionary<(string GlyphKey, bool Dark), BitmapImage> Cache = [];
    private static readonly Dictionary<string, bool> DarkMarkCache = [];

    internal static TextBlock IconGlyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = IconFontFamily,
        FontSize = size,
        FontWeight = FontWeights.Normal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

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

    /// <summary>
    /// True when the light-background logo is a near-black mark (mean luminance of its opaque
    /// pixels below a quarter), i.e. one that disappears when drawn directly on a dark surface.
    /// </summary>
    internal static bool IsDarkMark(string glyphKey)
    {
        if (string.IsNullOrWhiteSpace(glyphKey))
        {
            return false;
        }

        if (DarkMarkCache.TryGetValue(glyphKey, out var cached))
        {
            return cached;
        }

        var result = Get(glyphKey, darkBackground: false) is { } image && MeanOpaqueLuminance(image) < 0.25;
        DarkMarkCache[glyphKey] = result;
        return result;
    }

    private static double MeanOpaqueLuminance(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        double total = 0;
        var count = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] < 128)
            {
                continue;
            }

            total += ((0.2126 * pixels[offset + 2]) + (0.7152 * pixels[offset + 1]) + (0.0722 * pixels[offset])) / 255;
            count++;
        }

        return count == 0 ? 1 : total / count;
    }
}
