using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CodexWinBar.Core.Providers;

namespace CodexWinBar.Widget;

internal sealed class WidgetRenderer : IDisposable
{
    private const int LogoCacheLimit = 32;

    // Above this many providers the rich chip (logo + dual gauge + text) is too wide for a taskbar,
    // so the widget switches to compact chips: just the logo with a usage underline and incident dot.
    // The details stay one click away in the flyout. Chosen so the common 1-3 provider setups stay rich.
    private const int CompactChipThreshold = 3;
    private readonly ThemeReader _theme;
    private readonly Dictionary<(string GlyphKey, int TargetPixelSize, bool Dark), Bitmap> _logoCache = [];
    private Font? _font;
    private int _fontDpi;
    private float _fontSize;

    internal WidgetRenderer(ThemeReader theme)
    {
        _theme = theme;
    }

    internal int Measure(WidgetRenderState state, uint dpi) => Measure(state, dpi, false, null);

    /// <summary>
    /// Measures the widget along the taskbar. For a horizontal (bottom/top) taskbar this is the total
    /// WIDTH; for a vertical (left/right) taskbar it is the total HEIGHT. Fills <paramref name="chipBounds"/>
    /// with per-chip rects along that axis (the cross-axis dimension is filled in at hit-test time).
    /// </summary>
    internal int Measure(WidgetRenderState state, uint dpi, bool vertical, List<Rectangle>? chipBounds)
    {
        float scale = Scale(dpi);
        using Bitmap bitmap = new(1, 1);
        using Graphics graphics = Graphics.FromImage(bitmap);
        // Grayscale AA (not ClearType) — this surface is a per-pixel-alpha layered window, where
        // ClearType's subpixel blend assumes an opaque background and produces heavy, fringed text.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Font font = GetFont(dpi);
        chipBounds?.Clear();
        return vertical
            ? MeasureVertical(graphics, font, state, scale, chipBounds)
            : MeasureHorizontal(graphics, font, state, scale, chipBounds);
    }

    private int MeasureHorizontal(Graphics graphics, Font font, WidgetRenderState state, float scale, List<Rectangle>? chipBounds)
    {
        bool compact = IsCompact(state);
        int gap = Px(compact ? 6 : 10, scale);
        int width = 0;
        for (int i = 0; i < state.Chips.Count; i++)
        {
            WidgetChipState chip = state.Chips[i];
            int chipWidth = compact ? MeasureChipCompact(scale) : MeasureChip(graphics, font, chip, scale);
            chipBounds?.Add(new Rectangle(width, 0, chipWidth, 0));
            width += chipWidth;
            if (i < state.Chips.Count - 1)
            {
                width += gap;
            }
        }

        return Math.Max(Px(1, scale), width);
    }

    private int MeasureVertical(Graphics graphics, Font font, WidgetRenderState state, float scale, List<Rectangle>? chipBounds)
    {
        int gap = Px(10, scale);
        int height = 0;
        for (int i = 0; i < state.Chips.Count; i++)
        {
            int chipHeight = MeasureChipVertical(graphics, font, state.Chips[i], scale);
            chipBounds?.Add(new Rectangle(0, height, 0, chipHeight));
            height += chipHeight;
            if (i < state.Chips.Count - 1)
            {
                height += gap;
            }
        }

        return Math.Max(Px(1, scale), height);
    }

    private static bool IsCompact(WidgetRenderState state) => state.Chips.Count > CompactChipThreshold;

    internal bool RenderToWindow(IntPtr hwnd, WidgetRenderState state, int width, int height, uint dpi, bool vertical, int hoveredIndex, Point? screenLocation)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        using Bitmap bitmap = BuildBitmap(state, width, height, dpi, vertical, hoveredIndex);

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldObject = NativeMethods.SelectObject(memoryDc, bitmapHandle);
        try
        {
            NativeMethods.SIZE size = new() { CX = width, CY = height };
            NativeMethods.POINT source = new() { X = 0, Y = 0 };
            NativeMethods.BLENDFUNCTION blend = new()
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };
            if (screenLocation.HasValue)
            {
                NativeMethods.POINT destination = new() { X = screenLocation.Value.X, Y = screenLocation.Value.Y };
                return NativeMethods.UpdateLayeredWindow(hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
            }

            return UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            _ = NativeMethods.SelectObject(memoryDc, oldObject);
            _ = NativeMethods.DeleteObject(bitmapHandle);
            _ = NativeMethods.DeleteDC(memoryDc);
        }
    }

    /// <summary>Draws the widget to a 32bpp premultiplied-alpha bitmap (the surface RenderToWindow blits).</summary>
    internal Bitmap BuildBitmap(WidgetRenderState state, int width, int height, uint dpi, bool vertical, int hoveredIndex)
    {
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            if (vertical)
            {
                DrawVertical(graphics, state, width, height, dpi, hoveredIndex);
            }
            else
            {
                Draw(graphics, state, width, height, dpi, hoveredIndex);
            }
        }

        return bitmap;
    }

    public void Dispose()
    {
        _font?.Dispose();
        foreach (var bitmap in _logoCache.Values)
        {
            bitmap.Dispose();
        }
    }

    private void Draw(Graphics graphics, WidgetRenderState state, int width, int height, uint dpi, int hoveredIndex)
    {
        float scale = Scale(dpi);
        graphics.Clear(Color.FromArgb(1, 0, 0, 0));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Grayscale AA (not ClearType): on a per-pixel-alpha layered window ClearType can't produce a
        // correct alpha channel, which rendered the taskbar text heavy/fringed ("pixelated bold").
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Font font = GetFont(dpi);
        Color foreground = _theme.SystemUsesLightTheme ? Color.FromArgb(230, 27, 27, 27) : Color.FromArgb(230, 255, 255, 255);
        Color track = Color.FromArgb(64, foreground.R, foreground.G, foreground.B);
        bool compact = IsCompact(state);
        int gap = Px(compact ? 6 : 10, scale);
        int count = state.Chips.Count;
        var starts = new int[count];
        var lengths = new int[count];
        int x = 0;
        for (int i = 0; i < count; i++)
        {
            lengths[i] = compact ? MeasureChipCompact(scale) : MeasureChip(graphics, font, state.Chips[i], scale);
            starts[i] = x;
            x += lengths[i] + (i < count - 1 ? gap : 0);
        }

        // Hover highlight fills the hovered provider's whole region (midpoint to midpoint), matching the
        // click regions so hovering anywhere between icons lights up the provider you'd select.
        if (hoveredIndex >= 0 && hoveredIndex < count)
        {
            int left = hoveredIndex == 0 ? 0 : (starts[hoveredIndex - 1] + lengths[hoveredIndex - 1] + starts[hoveredIndex]) / 2;
            int right = hoveredIndex == count - 1 ? width : (starts[hoveredIndex] + lengths[hoveredIndex] + starts[hoveredIndex + 1]) / 2;
            DrawHoverHighlight(graphics, Rectangle.FromLTRB(left, 0, right, height), scale, foreground, vertical: false);
        }

        for (int i = 0; i < count; i++)
        {
            WidgetChipState chip = state.Chips[i];
            Rectangle bounds = new(starts[i], 0, lengths[i], height);
            if (compact)
            {
                DrawChipCompact(graphics, font, chip, bounds, scale, foreground, track);
            }
            else
            {
                DrawChip(graphics, font, chip, bounds, scale, foreground, track);
            }
        }
    }

    /// <summary>
    /// Compact chip for the many-providers case: the provider logo with a single usage underline
    /// (session, falling back to weekly) and an incident dot. No gauges-to-the-side or text, so eight
    /// providers still fit the taskbar; the numbers live in the flyout one click away.
    /// </summary>
    private void DrawChipCompact(Graphics graphics, Font font, WidgetChipState chip, Rectangle bounds, float scale, Color foreground, Color track)
    {
        float opacity = chip.IsStale ? 0.55f : 1.0f;
        int padding = Px(6, scale);
        int glyphSize = Px(18, scale);
        int gaugeHeight = Px(3, scale);
        int gaugeGap = Px(2, scale);
        int contentHeight = glyphSize + gaugeGap + gaugeHeight;
        int y = bounds.Top + (bounds.Height - contentHeight) / 2;
        int x = bounds.Left + padding;
        Color brand = Color.FromArgb(ApplyOpacity(255, opacity), chip.BrandR, chip.BrandG, chip.BrandB);

        if (this.GetLogo(chip.GlyphKey, glyphSize, dark: !_theme.SystemUsesLightTheme) is { } logo)
        {
            var previousInterpolation = graphics.InterpolationMode;
            var previousPixelOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(logo, new Rectangle(x, y, glyphSize, glyphSize));
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
        }
        else
        {
            using SolidBrush brandBrush = new(brand);
            graphics.FillEllipse(brandBrush, x, y, glyphSize, glyphSize);
            string letter = string.IsNullOrWhiteSpace(chip.GlyphKey) ? "?" : chip.GlyphKey[..1].ToUpperInvariant();
            using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using Font glyphFont = new(font.FontFamily, Math.Max(6, glyphSize * 0.62f), FontStyle.Bold, GraphicsUnit.Pixel);
            using SolidBrush glyphText = new(Color.FromArgb(ApplyOpacity(230, opacity), 255, 255, 255));
            graphics.DrawString(letter, glyphFont, glyphText, new RectangleF(x, y, glyphSize, glyphSize), centered);
        }

        // Usage underline directly beneath the logo. The track is always drawn so every chip shares a
        // baseline; the brand-colored fill shows session usage (or weekly when there's no session window).
        int gaugeY = y + glyphSize + gaugeGap;
        int radius = Math.Max(1, Px(2, scale));
        using (SolidBrush trackBrush = new(Color.FromArgb(ApplyOpacity(track.A, opacity), track.R, track.G, track.B)))
        {
            FillRoundRect(graphics, trackBrush, new Rectangle(x, gaugeY, glyphSize, gaugeHeight), radius);
        }

        double? usage = chip.SessionPercent ?? chip.WeeklyPercent;
        if (usage.HasValue)
        {
            int fillWidth = Math.Clamp((int)Math.Round(glyphSize * Math.Clamp(usage.Value, 0, 100) / 100.0), 0, glyphSize);
            if (fillWidth > 0)
            {
                using SolidBrush fillBrush = new(brand);
                FillRoundRect(graphics, fillBrush, new Rectangle(x, gaugeY, fillWidth, gaugeHeight), radius);
            }
        }

        if (chip.IncidentLevel > 0)
        {
            Color dot = chip.IncidentLevel == 1 ? Color.FromArgb(0xF8, 0xA8, 0x00) : Color.FromArgb(0xC4, 0x2B, 0x1C);
            using SolidBrush dotBrush = new(Color.FromArgb(ApplyOpacity(255, opacity), dot.R, dot.G, dot.B));
            int dotSize = Px(5, scale);
            graphics.FillEllipse(dotBrush, x + glyphSize - dotSize, y - Px(1, scale), dotSize, dotSize);
        }
    }

    private static int MeasureChipCompact(float scale) => Px(6, scale) * 2 + Px(18, scale);

    /// <summary>
    /// Vertical (left/right taskbar) layout: providers stacked down a narrow strip, each shown as the
    /// logo, then the session %, a short vertical-bar divider, then the weekly %.
    /// NOT YET LIVE-TESTED on a real side taskbar — verified only via offline renders (Windows 11 can't
    /// move the taskbar off the bottom). Check on Win10 or a Win11 taskbar tool before relying on it.
    /// </summary>
    private void DrawVertical(Graphics graphics, WidgetRenderState state, int width, int height, uint dpi, int hoveredIndex)
    {
        float scale = Scale(dpi);
        graphics.Clear(Color.FromArgb(1, 0, 0, 0));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Font font = GetFont(dpi);
        Color foreground = _theme.SystemUsesLightTheme ? Color.FromArgb(230, 27, 27, 27) : Color.FromArgb(230, 255, 255, 255);
        int gap = Px(10, scale);
        int count = state.Chips.Count;
        var starts = new int[count];
        var lengths = new int[count];
        int y = 0;
        for (int i = 0; i < count; i++)
        {
            lengths[i] = MeasureChipVertical(graphics, font, state.Chips[i], scale);
            starts[i] = y;
            y += lengths[i] + (i < count - 1 ? gap : 0);
        }

        if (hoveredIndex >= 0 && hoveredIndex < count)
        {
            int top = hoveredIndex == 0 ? 0 : (starts[hoveredIndex - 1] + lengths[hoveredIndex - 1] + starts[hoveredIndex]) / 2;
            int bottom = hoveredIndex == count - 1 ? height : (starts[hoveredIndex] + lengths[hoveredIndex] + starts[hoveredIndex + 1]) / 2;
            DrawHoverHighlight(graphics, Rectangle.FromLTRB(0, top, width, bottom), scale, foreground, vertical: true);
        }

        for (int i = 0; i < count; i++)
        {
            DrawChipVertical(graphics, font, state.Chips[i], new Rectangle(0, starts[i], width, lengths[i]), scale, foreground);
        }
    }

    private static void DrawHoverHighlight(Graphics graphics, Rectangle region, float scale, Color foreground, bool vertical)
    {
        int crossInset = Px(3, scale); // toward the taskbar-thickness edges
        int alongInset = Px(2, scale); // between providers
        Rectangle rect = vertical
            ? new Rectangle(region.Left + crossInset, region.Top + alongInset, Math.Max(1, region.Width - (2 * crossInset)), Math.Max(1, region.Height - (2 * alongInset)))
            : new Rectangle(region.Left + alongInset, region.Top + crossInset, Math.Max(1, region.Width - (2 * alongInset)), Math.Max(1, region.Height - (2 * crossInset)));
        using SolidBrush brush = new(Color.FromArgb(20, foreground.R, foreground.G, foreground.B));
        FillRoundRect(graphics, brush, rect, Px(8, scale));
    }

    private int MeasureChipVertical(Graphics graphics, Font font, WidgetChipState chip, float scale)
    {
        int glyph = Px(18, scale);
        int lineHeight = (int)Math.Ceiling(font.GetHeight(graphics));
        int dividerHeight = Px(9, scale);
        int vgap = Px(3, scale);
        int pad = Px(3, scale);
        int height = pad + glyph;
        bool hasSession = chip.SessionPercent.HasValue;
        bool hasWeekly = chip.WeeklyPercent.HasValue;
        if (hasSession)
        {
            height += vgap + lineHeight;
        }

        if (hasSession && hasWeekly)
        {
            height += vgap + dividerHeight;
        }

        if (hasWeekly)
        {
            height += vgap + lineHeight;
        }

        return height + pad;
    }

    private void DrawChipVertical(Graphics graphics, Font font, WidgetChipState chip, Rectangle bounds, float scale, Color foreground)
    {
        float opacity = chip.IsStale ? 0.55f : 1.0f;
        int glyph = Px(18, scale);
        int lineHeight = (int)Math.Ceiling(font.GetHeight(graphics));
        int dividerHeight = Px(9, scale);
        int vgap = Px(3, scale);
        int pad = Px(3, scale);
        int cx = bounds.Left + (bounds.Width / 2);
        Color brand = Color.FromArgb(ApplyOpacity(255, opacity), chip.BrandR, chip.BrandG, chip.BrandB);
        Color fg = Color.FromArgb(ApplyOpacity(foreground.A, opacity), foreground.R, foreground.G, foreground.B);

        int y = bounds.Top + pad;
        int glyphX = cx - (glyph / 2);
        if (this.GetLogo(chip.GlyphKey, glyph, dark: !_theme.SystemUsesLightTheme) is { } logo)
        {
            var previousInterpolation = graphics.InterpolationMode;
            var previousPixelOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(logo, new Rectangle(glyphX, y, glyph, glyph));
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
        }
        else
        {
            using SolidBrush brandBrush = new(brand);
            graphics.FillEllipse(brandBrush, glyphX, y, glyph, glyph);
            string letter = string.IsNullOrWhiteSpace(chip.GlyphKey) ? "?" : chip.GlyphKey[..1].ToUpperInvariant();
            using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using Font glyphFont = new(font.FontFamily, Math.Max(6, glyph * 0.62f), FontStyle.Bold, GraphicsUnit.Pixel);
            using SolidBrush glyphText = new(Color.FromArgb(ApplyOpacity(230, opacity), 255, 255, 255));
            graphics.DrawString(letter, glyphFont, glyphText, new RectangleF(glyphX, y, glyph, glyph), centered);
        }

        if (chip.IncidentLevel > 0)
        {
            Color dot = chip.IncidentLevel == 1 ? Color.FromArgb(0xF8, 0xA8, 0x00) : Color.FromArgb(0xC4, 0x2B, 0x1C);
            using SolidBrush dotBrush = new(Color.FromArgb(ApplyOpacity(255, opacity), dot.R, dot.G, dot.B));
            int dotSize = Px(5, scale);
            graphics.FillEllipse(dotBrush, glyphX + glyph - dotSize, y - Px(1, scale), dotSize, dotSize);
        }

        y += glyph;

        using StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using SolidBrush textBrush = new(fg);
        bool hasSession = chip.SessionPercent.HasValue;
        bool hasWeekly = chip.WeeklyPercent.HasValue;
        if (hasSession)
        {
            y += vgap;
            graphics.DrawString(FormatPercent(chip.SessionPercent!.Value), font, textBrush, new RectangleF(bounds.Left, y, bounds.Width, lineHeight), center);
            y += lineHeight;
        }

        if (hasSession && hasWeekly)
        {
            y += vgap;
            int dividerWidth = Math.Max(1, Px(2, scale));
            using SolidBrush dividerBrush = new(Color.FromArgb(ApplyOpacity(120, opacity), foreground.R, foreground.G, foreground.B));
            FillRoundRect(graphics, dividerBrush, new Rectangle(cx - (dividerWidth / 2), y, dividerWidth, dividerHeight), Math.Max(1, dividerWidth / 2));
            y += dividerHeight;
        }

        if (hasWeekly)
        {
            y += vgap;
            graphics.DrawString(FormatPercent(chip.WeeklyPercent!.Value), font, textBrush, new RectangleF(bounds.Left, y, bounds.Width, lineHeight), center);
        }
    }

    private static string FormatPercent(double value) => ((int)Math.Round(Math.Clamp(value, 0, 100))).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    private void DrawChip(Graphics graphics, Font font, WidgetChipState chip, Rectangle bounds, float scale, Color foreground, Color track)
    {
        float opacity = chip.IsStale ? 0.55f : 1.0f;
        int padding = Px(8, scale);
        int glyphSize = Px(12, scale);
        int gaugeWidth = Px(16, scale);
        int gaugeHeight = Px(4, scale);
        int gaugeGap = Px(2, scale);
        int gap = Px(6, scale);

        int contentHeight = Math.Max(glyphSize, Math.Max(Px(10, scale), (int)Math.Ceiling(font.GetHeight(graphics))));
        int y = bounds.Top + (bounds.Height - contentHeight) / 2;
        int x = bounds.Left + padding;
        Color brand = Color.FromArgb(ApplyOpacity(255, opacity), chip.BrandR, chip.BrandG, chip.BrandB);
        Color fg = Color.FromArgb(ApplyOpacity(foreground.A, opacity), foreground.R, foreground.G, foreground.B);

        int glyphY = y + (contentHeight - glyphSize) / 2;
        if (this.GetLogo(chip.GlyphKey, glyphSize, dark: !_theme.SystemUsesLightTheme) is { } logo)
        {
            var previousInterpolation = graphics.InterpolationMode;
            var previousPixelOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(logo, new Rectangle(x, glyphY, glyphSize, glyphSize));
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
        }
        else
        {
            using SolidBrush brandBrush = new(brand);
            graphics.FillEllipse(brandBrush, x, glyphY, glyphSize, glyphSize);
            string letter = string.IsNullOrWhiteSpace(chip.GlyphKey) ? "?" : chip.GlyphKey[..1].ToUpperInvariant();
            using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using Font glyphFont = new(font.FontFamily, Math.Max(6, glyphSize * 0.62f), FontStyle.Bold, GraphicsUnit.Pixel);
            using SolidBrush glyphText = new(Color.FromArgb(ApplyOpacity(230, opacity), 255, 255, 255));
            graphics.DrawString(letter, glyphFont, glyphText, new RectangleF(x, glyphY, glyphSize, glyphSize), centered);
        }

        if (chip.IncidentLevel > 0)
        {
            Color dot = chip.IncidentLevel == 1 ? Color.FromArgb(0xF8, 0xA8, 0x00) : Color.FromArgb(0xC4, 0x2B, 0x1C);
            using SolidBrush dotBrush = new(Color.FromArgb(ApplyOpacity(255, opacity), dot.R, dot.G, dot.B));
            int dotSize = Px(4, scale);
            graphics.FillEllipse(dotBrush, x + glyphSize - dotSize / 2, glyphY - dotSize / 2, dotSize, dotSize);
        }

        x += glyphSize + gap;
        if (chip.SessionPercent.HasValue || chip.WeeklyPercent.HasValue)
        {
            int gaugesY = y + (contentHeight - (gaugeHeight * 2 + gaugeGap)) / 2;
            DrawGauge(graphics, x, gaugesY, gaugeWidth, gaugeHeight, chip.SessionPercent, track, brand, opacity, scale);
            DrawGauge(graphics, x, gaugesY + gaugeHeight + gaugeGap, gaugeWidth, gaugeHeight, chip.WeeklyPercent, track, brand, opacity, scale);
            x += gaugeWidth + gap;
        }

        if (!string.IsNullOrWhiteSpace(chip.Text))
        {
            using SolidBrush textBrush = new(fg);
            graphics.DrawString(chip.Text, font, textBrush, new PointF(x, y + (contentHeight - font.GetHeight(graphics)) / 2));
        }
    }

    private static void DrawGauge(Graphics graphics, int x, int y, int width, int height, double? value, Color track, Color fill, float opacity, float scale)
    {
        if (!value.HasValue)
        {
            return;
        }

        int radius = Math.Max(1, Px(2, scale));
        using SolidBrush trackBrush = new(Color.FromArgb(ApplyOpacity(track.A, opacity), track.R, track.G, track.B));
        FillRoundRect(graphics, trackBrush, new Rectangle(x, y, width, height), radius);
        int fillWidth = Math.Clamp((int)Math.Round(width * Math.Clamp(value.Value, 0, 100) / 100.0), 0, width);
        if (fillWidth > 0)
        {
            using SolidBrush fillBrush = new(fill);
            FillRoundRect(graphics, fillBrush, new Rectangle(x, y, fillWidth, height), radius);
        }
    }

    private int MeasureChip(Graphics graphics, Font font, WidgetChipState chip, float scale)
    {
        int padding = Px(8, scale) * 2;
        int glyph = Px(12, scale);
        int gap = Px(6, scale);
        int width = padding + glyph;
        if (chip.SessionPercent.HasValue || chip.WeeklyPercent.HasValue)
        {
            width += gap + Px(16, scale);
        }

        if (!string.IsNullOrWhiteSpace(chip.Text))
        {
            SizeF text = graphics.MeasureString(chip.Text, font, int.MaxValue, StringFormat.GenericTypographic);
            width += gap + (int)Math.Ceiling(text.Width);
        }

        return width;
    }

    private Font GetFont(uint dpi)
    {
        float scale = Scale(dpi);
        float size = 12.5f * scale;
        if (_font is not null && _fontDpi == dpi && Math.Abs(_fontSize - size) < 0.01f)
        {
            return _font;
        }

        _font?.Dispose();
        try
        {
            _font = new Font("Segoe UI Variable Text", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            _font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        _fontDpi = (int)dpi;
        _fontSize = size;
        return _font;
    }

    private Bitmap? GetLogo(string glyphKey, int targetPixelSize, bool dark)
    {
        if (string.IsNullOrWhiteSpace(glyphKey))
        {
            return null;
        }

        var key = (glyphKey, targetPixelSize, dark);
        if (_logoCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bytes = ProviderAssets.GetLogoPng(glyphKey, dark);
        if (bytes is null)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes);
        using var source = new Bitmap(stream);
        var scaled = new Bitmap(targetPixelSize, targetPixelSize, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, targetPixelSize, targetPixelSize));
        }

        if (_logoCache.Count >= LogoCacheLimit)
        {
            var oldestKey = _logoCache.Keys.First();
            _logoCache[oldestKey].Dispose();
            _logoCache.Remove(oldestKey);
        }

        _logoCache[key] = scaled;
        return scaled;
    }

    private static void FillRoundRect(Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        using GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    private static int Px(double value, float scale) => Math.Max(1, (int)Math.Round(value * scale));

    private static float Scale(uint dpi) => Math.Max(1, dpi) / 96f;

    private static int ApplyOpacity(int alpha, float opacity) => Math.Clamp((int)Math.Round(alpha * opacity), 0, 255);

    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        IntPtr pptDst,
        ref NativeMethods.SIZE psize,
        IntPtr hdcSrc,
        ref NativeMethods.POINT pptSrc,
        uint crKey,
        ref NativeMethods.BLENDFUNCTION pblend,
        uint dwFlags);
}
