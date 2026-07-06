using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexWinBar.App.Settings;

public sealed class ThresholdTrack : FrameworkElement
{
    private const double TrackInset = 12;
    private const double TrackY = 40;
    private const double TrackHeight = 4;
    private const double DotSize = 14;
    private const double ActiveDotSize = 17;
    private const double HitRadius = 10;
    private const double DragThreshold = 3;

    private readonly List<int> values = [];
    private int? hoveredValue;
    private int? hoverAddValue;
    private DragState? drag;

    public ThresholdTrack()
    {
        this.Height = 56;
        this.MinWidth = 320;
        this.Focusable = false;
        this.IsEnabledChanged += (_, _) =>
        {
            this.Opacity = this.IsEnabled ? 1.0 : 0.55;
            this.Cursor = this.IsEnabled ? null : Cursors.Arrow;
            this.InvalidateVisual();
        };
    }

    public event Action<IReadOnlyList<int>>? ValuesChanged;

    public IReadOnlyList<int> Values
    {
        get => this.values;
        set
        {
            this.values.Clear();
            this.values.AddRange(Normalize(value));
            this.InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        var width = this.ActualWidth;
        if (width <= TrackInset * 2)
        {
            return;
        }

        var foreground = this.Brush("SettingsForeground", Brushes.Black);
        var muted = this.Brush("SettingsMutedForeground", foreground);
        var accent = this.Brush("SettingsAccent", Brushes.DeepSkyBlue);
        var popupBackground = this.Brush("SettingsComboPopupBackground", Brushes.White);
        var popupBorder = this.Brush("SettingsComboPopupBorder", muted);
        var ring = this.DotRingBrush(popupBackground);
        var trackLeft = TrackInset;
        var trackWidth = width - TrackInset * 2;
        var trackTop = TrackY - TrackHeight / 2;

        context.DrawRoundedRectangle(
            foreground.WithOpacity(0.15),
            null,
            new Rect(trackLeft, trackTop, trackWidth, TrackHeight),
            TrackHeight / 2,
            TrackHeight / 2);

        context.PushGuidelineSet(new GuidelineSet());
        try
        {
            for (var tick = 0; tick <= 100; tick += 10)
            {
                var x = this.XForValue(tick);
                context.DrawRectangle(
                    foreground.WithOpacity(0.25),
                    null,
                    new Rect(Math.Round(x) - 0.5, TrackY - 6, 1, 6));
            }
        }
        finally
        {
            context.Pop();
        }

        this.DrawLabel(context, "0", muted, trackLeft, TrackY + 7, TextAlignment.Left);
        this.DrawLabel(context, "100", muted, trackLeft + trackWidth, TrackY + 7, TextAlignment.Right);

        foreach (var value in this.values)
        {
            if (this.drag?.OriginalValue == value)
            {
                continue;
            }

            var active = this.hoveredValue == value;
            this.DrawDot(context, value, active ? ActiveDotSize : DotSize, accent, ring);
        }

        if (this.drag is { } state)
        {
            this.DrawDot(context, state.LiveValue, ActiveDotSize, accent, ring);
            var caption = state.HasDragged
                ? state.LiveValue.ToString(CultureInfo.InvariantCulture)
                : state.IsNew
                    ? $"Add {state.LiveValue}"
                    : $"{state.LiveValue} - release to remove";
            if (!state.HasDragged && !state.IsNew)
            {
                this.DrawRemoveGlyph(context, state.LiveValue, ring);
            }

            this.DrawBubble(context, state.LiveValue, caption, popupBackground, popupBorder, foreground);
        }
        else if (this.hoveredValue is { } hover && this.values.Contains(hover))
        {
            this.DrawDot(context, hover, ActiveDotSize, accent, ring);
            this.DrawRemoveGlyph(context, hover, ring);
            this.DrawBubble(context, hover, $"{hover} - click to remove", popupBackground, popupBorder, foreground);
        }
        else if (this.hoverAddValue is { } add)
        {
            // Ghost preview: clicking an empty spot on the track adds a marker here.
            this.DrawDot(context, add, DotSize, accent.WithOpacity(0.45), ring.WithOpacity(0.45));
            this.DrawAddGlyph(context, add, ring.WithOpacity(0.8));
            this.DrawBubble(context, add, $"Add {add}", popupBackground, popupBorder, foreground);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        if (!this.IsEnabled)
        {
            return;
        }

        var point = args.GetPosition(this);
        var hit = this.HitTestValue(point);
        if (hit is not null)
        {
            this.drag = new DragState(hit.Value, hit.Value, hit.Value, point, false, false);
            this.hoverAddValue = null;
            this.CaptureMouse();
            args.Handled = true;
            this.InvalidateVisual();
            return;
        }

        if (Math.Abs(point.Y - TrackY) <= 14)
        {
            var value = this.ValueForX(point.X);
            this.drag = new DragState(null, value, value, point, true, false);
            this.hoverAddValue = null;
            this.CaptureMouse();
            args.Handled = true;
            this.InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        if (!this.IsEnabled)
        {
            return;
        }

        var point = args.GetPosition(this);
        if (this.drag is { } state)
        {
            var moved = state.HasDragged || Distance(point, state.StartPoint) > DragThreshold;
            this.drag = state with
            {
                LiveValue = moved ? this.ValueForX(point.X) : state.StartValue,
                HasDragged = moved,
            };
            this.InvalidateVisual();
            return;
        }

        var hit = this.HitTestValue(point);
        this.hoveredValue = hit;
        var onTrack = Math.Abs(point.Y - TrackY) <= 14;
        var addCandidate = hit is null && onTrack ? this.ValueForX(point.X) : (int?)null;
        this.hoverAddValue = addCandidate is { } candidate && !this.values.Contains(candidate) ? candidate : null;
        this.Cursor = hit is not null || onTrack ? Cursors.Hand : null;
        this.InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        if (this.drag is not { } state)
        {
            return;
        }

        this.ReleaseMouseCapture();
        this.drag = null;

        if (state.OriginalValue is { } original)
        {
            this.values.Remove(original);
            if (state.HasDragged)
            {
                this.values.Add(state.LiveValue);
            }
        }
        else
        {
            this.values.Add(state.LiveValue);
        }

        this.ResetValues();
        this.hoveredValue = state.LiveValue;
        // After a click-delete the cursor still sits on the empty spot: show the add ghost right
        // away so re-adding the marker (undo) is a single click.
        this.hoverAddValue = this.values.Contains(state.LiveValue) ? null : state.LiveValue;
        this.ValuesChanged?.Invoke(this.values.ToArray());
        args.Handled = true;
        this.InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        if (this.drag is null)
        {
            this.hoveredValue = null;
            this.hoverAddValue = null;
            this.Cursor = null;
            this.InvalidateVisual();
        }

        base.OnMouseLeave(args);
    }

    private int? HitTestValue(Point point)
    {
        foreach (var value in this.values)
        {
            if (Math.Abs(point.X - this.XForValue(value)) <= HitRadius && Math.Abs(point.Y - TrackY) <= HitRadius)
            {
                return value;
            }
        }

        return null;
    }

    private void DrawDot(DrawingContext context, int value, double size, Brush fill, Brush ring)
    {
        var center = new Point(this.XForValue(value), TrackY);
        context.DrawEllipse(fill, new Pen(ring, 2), center, size / 2, size / 2);
    }

    /// <summary>Draws an "x" inside a marker dot to signal that clicking it removes the threshold.</summary>
    private void DrawRemoveGlyph(DrawingContext context, int value, Brush stroke)
    {
        var center = new Point(this.XForValue(value), TrackY);
        const double arm = 3;
        var pen = new Pen(stroke, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.DrawLine(pen, new Point(center.X - arm, center.Y - arm), new Point(center.X + arm, center.Y + arm));
        context.DrawLine(pen, new Point(center.X - arm, center.Y + arm), new Point(center.X + arm, center.Y - arm));
    }

    /// <summary>Draws a "+" inside the ghost dot that previews where a click adds a threshold.</summary>
    private void DrawAddGlyph(DrawingContext context, int value, Brush stroke)
    {
        var center = new Point(this.XForValue(value), TrackY);
        const double arm = 3;
        var pen = new Pen(stroke, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.DrawLine(pen, new Point(center.X - arm, center.Y), new Point(center.X + arm, center.Y));
        context.DrawLine(pen, new Point(center.X, center.Y - arm), new Point(center.X, center.Y + arm));
    }

    private void DrawBubble(DrawingContext context, int value, string caption, Brush background, Brush border, Brush foreground)
    {
        var text = this.Formatted(caption, 12, foreground, TextAlignment.Left);
        var width = text.WidthIncludingTrailingWhitespace + 16;
        var height = text.Height + 8;
        var x = Math.Clamp(this.XForValue(value) - width / 2, 0, Math.Max(0, this.ActualWidth - width));
        var rect = new Rect(x, 2, width, height);

        context.DrawRoundedRectangle(background, new Pen(border, 1), rect, 6, 6);
        context.DrawText(text, new Point(rect.X + 8, rect.Y + 4));
    }

    private void DrawLabel(
        DrawingContext context,
        string text,
        Brush brush,
        double x,
        double y,
        TextAlignment alignment)
    {
        var formatted = this.Formatted(text, 11, brush, alignment);
        var point = alignment == TextAlignment.Right
            ? new Point(x - formatted.WidthIncludingTrailingWhitespace, y)
            : new Point(x, y);
        context.DrawText(formatted, point);
    }

    private FormattedText Formatted(string text, double size, Brush brush, TextAlignment alignment)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment,
        };
        return formatted;
    }

    private Brush DotRingBrush(Brush popupBackground)
    {
        if (popupBackground is SolidColorBrush { Color: var color } &&
            color.R < 0x80 &&
            color.G < 0x80 &&
            color.B < 0x80)
        {
            return new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x2c));
        }

        return Brushes.White;
    }

    private Brush Brush(string key, Brush fallback)
    {
        return this.TryFindResource(key) as Brush ?? fallback;
    }

    private double XForValue(int value)
    {
        var usable = Math.Max(1, this.ActualWidth - TrackInset * 2);
        return TrackInset + Math.Clamp(value, 0, 100) / 100.0 * usable;
    }

    private int ValueForX(double x)
    {
        var usable = Math.Max(1, this.ActualWidth - TrackInset * 2);
        var value = Math.Round((x - TrackInset) / usable * 100, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(value, 0, 99);
    }

    private void ResetValues()
    {
        var normalized = Normalize(this.values);
        this.values.Clear();
        this.values.AddRange(normalized);
    }

    private static IReadOnlyList<int> Normalize(IEnumerable<int> source)
    {
        return source
            .Select(value => Math.Clamp(value, 0, 99))
            .Distinct()
            .OrderDescending()
            .ToArray();
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed record DragState(
        int? OriginalValue,
        int StartValue,
        int LiveValue,
        Point StartPoint,
        bool IsNew,
        bool HasDragged);
}

internal static class ThresholdTrackBrushExtensions
{
    public static Brush WithOpacity(this Brush brush, double opacity)
    {
        var clone = brush.CloneCurrentValue();
        clone.Opacity *= opacity;
        return clone;
    }
}
