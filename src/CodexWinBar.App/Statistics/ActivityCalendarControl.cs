using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexWinBar.App.Statistics;

internal sealed class ActivityCalendarControl : FrameworkElement
{
    private static readonly FontFamily CalendarFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly CultureInfo UiCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly IReadOnlyList<ActivityDay> days;
    private readonly IReadOnlyDictionary<DateOnly, ActivityDay> daysByDate;
    private readonly Color accent;
    private readonly bool isDark;
    private readonly ActivityScaleMode scaleMode;
    private readonly DateOnly lastSelectableDate;
    private DateOnly selectedDate;
    private DateOnly? hoverDate;

    internal ActivityCalendarControl(
        IReadOnlyList<ActivityDay> days,
        DateOnly selectedDate,
        DateOnly lastSelectableDate,
        Color accent,
        bool isDark,
        ActivityScaleMode scaleMode)
    {
        this.days = days;
        this.daysByDate = days.ToDictionary(day => day.Date);
        this.selectedDate = selectedDate;
        this.lastSelectableDate = lastSelectableDate;
        this.accent = accent;
        this.isDark = isDark;
        this.scaleMode = scaleMode;
        this.Focusable = true;
        this.Cursor = Cursors.Hand;
        this.MinHeight = 148;
        AutomationProperties.SetName(this, "Daily activity calendar");
        AutomationProperties.SetHelpText(this, "Use arrow keys to move by day or week, then press Enter to show hourly details.");
    }

    internal event Action<DateOnly>? DateSelected;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var layout = this.Layout();
        if (layout.CellSize <= 0)
        {
            return;
        }

        var muted = this.isDark ? Color.FromRgb(167, 173, 184) : Color.FromRgb(98, 104, 116);
        var unavailable = this.isDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 23, 25, 29);
        var noActivity = this.isDark ? Color.FromArgb(44, 255, 255, 255) : Color.FromArgb(28, 23, 25, 29);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var row in new[] { 1, 3, 5 })
        {
            var label = UiCulture.DateTimeFormat.AbbreviatedDayNames[row];
            this.DrawText(drawingContext, label, muted, 10.5, new Point(0, layout.Top + (row * layout.Step) - 1), dpi);
        }

        var lastMonth = -1;
        for (var week = 0; week < layout.Weeks; week++)
        {
            var first = layout.Start.AddDays(week * 7);
            if (first.Month != lastMonth && (week == 0 || first.Day <= 7))
            {
                var label = first.ToDateTime(TimeOnly.MinValue).ToString("MMM", UiCulture);
                this.DrawText(
                    drawingContext,
                    label,
                    muted,
                    10.5,
                    new Point(layout.Left + (week * layout.Step), 0),
                    dpi);
                lastMonth = first.Month;
            }
        }

        foreach (var day in this.days)
        {
            var rect = this.CellRect(day.Date, layout);
            if (rect is null)
            {
                continue;
            }

            var fill = day.Date > DateOnly.FromDateTime(DateTime.Today)
                ? unavailable
                : day.HasCoverage
                    ? day.Value <= 0.001
                        ? noActivity
                        : IntensityColor(this.accent, day.Intensity(this.scaleMode), this.isDark)
                    : unavailable;
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect.Value, 2.5, 2.5);
            if (day.Date == this.selectedDate)
            {
                var outline = this.isDark ? Colors.White : Color.FromRgb(23, 25, 29);
                drawingContext.DrawRoundedRectangle(
                    null,
                    new Pen(new SolidColorBrush(outline), 1.5),
                    new Rect(rect.Value.X - 2, rect.Value.Y - 2, rect.Value.Width + 4, rect.Value.Height + 4),
                    4,
                    4);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var date = this.DateAt(e.GetPosition(this));
        if (date == this.hoverDate)
        {
            return;
        }

        this.hoverDate = date;
        this.ToolTip = date is { } value && this.daysByDate.TryGetValue(value, out var day)
            ? Tooltip(day, this.scaleMode)
            : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        this.hoverDate = null;
        this.ToolTip = null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        this.Focus();
        if (this.DateAt(e.GetPosition(this)) is { } date && this.CanSelect(date))
        {
            this.Select(date);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var next = e.Key switch
        {
            Key.Left => this.selectedDate.AddDays(-7),
            Key.Right => this.selectedDate.AddDays(7),
            Key.Up => this.selectedDate.AddDays(-1),
            Key.Down => this.selectedDate.AddDays(1),
            _ => this.selectedDate,
        };
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (this.CanSelect(next))
            {
                this.selectedDate = next;
                this.InvalidateVisual();
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            this.Select(this.selectedDate);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void Select(DateOnly date)
    {
        if (!this.CanSelect(date))
        {
            return;
        }

        this.selectedDate = date;
        this.InvalidateVisual();
        this.DateSelected?.Invoke(date);
    }

    private DateOnly? DateAt(Point point)
    {
        var layout = this.Layout();
        var week = (int)Math.Floor((point.X - layout.Left) / layout.Step);
        var row = (int)Math.Floor((point.Y - layout.Top) / layout.Step);
        if (week < 0 || week >= layout.Weeks || row < 0 || row >= 7)
        {
            return null;
        }

        var withinX = (point.X - layout.Left) - (week * layout.Step);
        var withinY = (point.Y - layout.Top) - (row * layout.Step);
        if (withinX > layout.CellSize || withinY > layout.CellSize)
        {
            return null;
        }

        return layout.Start.AddDays((week * 7) + row);
    }

    private Rect? CellRect(DateOnly date, CalendarLayout layout)
    {
        var offset = date.DayNumber - layout.Start.DayNumber;
        if (offset < 0 || offset >= layout.Weeks * 7)
        {
            return null;
        }

        var week = offset / 7;
        var row = offset % 7;
        return new Rect(
            layout.Left + (week * layout.Step),
            layout.Top + (row * layout.Step),
            layout.CellSize,
            layout.CellSize);
    }

    private CalendarLayout Layout()
    {
        const double left = 30;
        const double top = 22;
        const double gap = 3;
        var weeks = Math.Max(1, this.days.Count / 7);
        var available = Math.Max(0, this.ActualWidth - left);
        var cell = Math.Min(13, Math.Max(6, (available - ((weeks - 1) * gap)) / weeks));
        return new CalendarLayout(this.days[0].Date, weeks, left, top, cell, cell + gap);
    }

    private static string Tooltip(ActivityDay day, ActivityScaleMode mode)
    {
        if (!day.HasCoverage)
        {
            return $"{day.Date.ToString("dddd, MMM d", UiCulture)}\nNo observation data";
        }

        return $"{day.Date.ToString("dddd, MMM d", UiCulture)}\n{day.Value:0.#} quota points observed\n" +
            $"{day.ActiveHours} active hours · {mode} scale level {day.Intensity(mode)}/4";
    }

    private bool CanSelect(DateOnly date) => date <= this.lastSelectableDate && this.daysByDate.ContainsKey(date);

    private static Color IntensityColor(Color accent, int level, bool isDark)
    {
        var alpha = level switch
        {
            1 => 74,
            2 => 124,
            3 => 184,
            _ => 242,
        };
        if (isDark && level == 1)
        {
            alpha += 20;
        }

        return Color.FromArgb((byte)alpha, accent.R, accent.G, accent.B);
    }

    private void DrawText(
        DrawingContext drawingContext,
        string text,
        Color color,
        double size,
        Point origin,
        double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            UiCulture,
            FlowDirection.LeftToRight,
            new Typeface(CalendarFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            pixelsPerDip);
        drawingContext.DrawText(formatted, origin);
    }

    private sealed record CalendarLayout(
        DateOnly Start,
        int Weeks,
        double Left,
        double Top,
        double CellSize,
        double Step);
}
