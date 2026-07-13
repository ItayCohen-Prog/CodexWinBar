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
    internal const string SettingsGlyph = "\uE713";
    internal const string CloseGlyph = "\uE711";
    internal const string CopyGlyph = "\uE8C8";
    internal const string SignInGlyph = "\uE77B";
    internal const string WarningGlyph = "\uE7BA";
    internal const string ExternalLinkGlyph = "\uE8A7";
    internal const string DownloadGlyph = "\uE896";

    private static readonly FontFamily IconFontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Dictionary<(string GlyphKey, bool Dark), BitmapImage> Cache = [];

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
}
