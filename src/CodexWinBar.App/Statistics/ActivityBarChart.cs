using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexWinBar.App.Statistics;

internal sealed record ActivityBar(string Label, double Value, string Description);

internal sealed class ActivityBarChart : FrameworkElement
{
    private static readonly FontFamily ChartFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly CultureInfo UiCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly IReadOnlyList<ActivityBar> bars;
    private readonly Color accent;
    private readonly bool isDark;
    private int selectedIndex;
    private int hoverIndex = -1;

    internal ActivityBarChart(
        IReadOnlyList<ActivityBar> bars,
        int selectedIndex,
        Color accent,
        bool isDark)
    {
        this.bars = bars;
        this.selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, bars.Count - 1));
        this.accent = accent;
        this.isDark = isDark;
        this.Focusable = true;
        this.Cursor = Cursors.Hand;
        this.MinHeight = 190;
        AutomationProperties.SetName(this, "Activity bar chart");
        AutomationProperties.SetHelpText(this, string.Join("; ", bars.Select(bar => bar.Description)));
    }

    internal event Action<int>? BarSelected;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var plot = this.PlotRect();
        if (plot.Width <= 0 || plot.Height <= 0 || this.bars.Count == 0)
        {
            return;
        }

        var muted = this.isDark ? Color.FromRgb(167, 173, 184) : Color.FromRgb(98, 104, 116);
        var grid = this.isDark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(30, 23, 25, 29);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var max = Math.Max(1, this.bars.Max(bar => bar.Value));
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, this.ActualWidth, this.ActualHeight)));
        foreach (var fraction in new[] { 1.0, 0.5, 0.0 })
        {
            var y = plot.Bottom - (plot.Height * fraction);
            drawingContext.DrawLine(new Pen(new SolidColorBrush(grid), 1), new Point(plot.Left, y), new Point(plot.Right, y));
            this.DrawText(drawingContext, $"{max * fraction:0.#}", muted, 10.5, new Point(0, y - 7), dpi);
        }

        var slot = plot.Width / this.bars.Count;
        var width = Math.Clamp(slot * 0.56, 4, 32);
        for (var index = 0; index < this.bars.Count; index++)
        {
            var bar = this.bars[index];
            var height = bar.Value <= 0 ? 2 : Math.Max(3, plot.Height * bar.Value / max);
            var x = plot.Left + (index * slot) + ((slot - width) / 2);
            var rect = new Rect(x, plot.Bottom - height, width, height);
            var alpha = index == this.selectedIndex ? (byte)242 : (byte)150;
            drawingContext.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(alpha, this.accent.R, this.accent.G, this.accent.B)),
                null,
                rect,
                3,
                3);
            if (index == this.selectedIndex)
            {
                drawingContext.DrawRoundedRectangle(
                    null,
                    new Pen(new SolidColorBrush(this.accent), 1.5),
                    new Rect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4),
                    4,
                    4);
            }

            if (this.ShouldLabel(index))
            {
                var formatted = this.FormatText(bar.Label, muted, 10.5, dpi);
                var center = x + (width / 2);
                var labelX = Math.Clamp(center - (formatted.Width / 2), plot.Left, Math.Max(plot.Left, plot.Right - formatted.Width));
                drawingContext.DrawText(formatted, new Point(labelX, plot.Bottom + 8));
            }
        }

        drawingContext.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = this.IndexAt(e.GetPosition(this));
        if (index == this.hoverIndex)
        {
            return;
        }

        this.hoverIndex = index;
        this.ToolTip = index >= 0 ? this.bars[index].Description : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        this.hoverIndex = -1;
        this.ToolTip = null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        this.Focus();
        var index = this.IndexAt(e.GetPosition(this));
        if (index >= 0)
        {
            this.Select(index);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Up)
        {
            this.selectedIndex = Math.Max(0, this.selectedIndex - 1);
            this.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Right or Key.Down)
        {
            this.selectedIndex = Math.Min(this.bars.Count - 1, this.selectedIndex + 1);
            this.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            this.Select(this.selectedIndex);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void Select(int index)
    {
        this.selectedIndex = index;
        this.InvalidateVisual();
        this.BarSelected?.Invoke(index);
    }

    private int IndexAt(Point point)
    {
        var plot = this.PlotRect();
        if (point.X < plot.Left || point.X > plot.Right || point.Y < plot.Top || point.Y > plot.Bottom || this.bars.Count == 0)
        {
            return -1;
        }

        return Math.Clamp((int)((point.X - plot.Left) / (plot.Width / this.bars.Count)), 0, this.bars.Count - 1);
    }

    private bool ShouldLabel(int index) => this.bars.Count <= 8 || index % 3 == 0;

    private Rect PlotRect() => new(38, 10, Math.Max(0, this.ActualWidth - 48), Math.Max(0, this.ActualHeight - 42));

    private void DrawText(DrawingContext context, string text, Color color, double size, Point origin, double dpi) =>
        context.DrawText(this.FormatText(text, color, size, dpi), origin);

    private FormattedText FormatText(string text, Color color, double size, double dpi) => new(
        text,
        UiCulture,
        FlowDirection.LeftToRight,
        new Typeface(ChartFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size,
        new SolidColorBrush(color),
        dpi);
}
