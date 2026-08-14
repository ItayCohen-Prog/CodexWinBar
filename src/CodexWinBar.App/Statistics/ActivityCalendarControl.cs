using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexWinBar.App.Statistics;

internal sealed class ActivityCalendarControl : FrameworkElement
{
    private static readonly DependencyProperty HoverOpacityProperty = DependencyProperty.Register(
        nameof(HoverOpacity),
        typeof(double),
        typeof(ActivityCalendarControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly FontFamily CalendarFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly CultureInfo UiCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly IReadOnlyList<ActivityDay> days;
    private readonly IReadOnlyDictionary<DateOnly, ActivityDay> daysByDate;
    private readonly Color accent;
    private readonly bool isDark;
    private readonly Func<double, string> valueFormatter;
    private readonly DateOnly firstSelectableDate;
    private readonly DateOnly lastSelectableDate;
    private DateOnly selectedDate;
    private DateOnly? hoverDate;

    private double HoverOpacity
    {
        get => (double)this.GetValue(HoverOpacityProperty);
        set => this.SetValue(HoverOpacityProperty, value);
    }

    internal ActivityCalendarControl(
        IReadOnlyList<ActivityDay> days,
        DateOnly selectedDate,
        DateOnly firstSelectableDate,
        DateOnly lastSelectableDate,
        Color accent,
        bool isDark,
        Func<double, string> valueFormatter)
    {
        this.days = days;
        this.daysByDate = days.ToDictionary(day => day.Date);
        this.selectedDate = selectedDate;
        this.firstSelectableDate = firstSelectableDate;
        this.lastSelectableDate = lastSelectableDate;
        this.accent = accent;
        this.isDark = isDark;
        this.valueFormatter = valueFormatter;
        this.Focusable = true;
        this.Cursor = Cursors.Hand;
        this.MinHeight = 148;
        AutomationProperties.SetName(this, "Observed allowance activity calendar");
        AutomationProperties.SetHelpText(this, "Use arrow keys to inspect days, Home or End to jump, then press Enter to open the selected month.");
    }

    internal event Action<DateOnly>? DateSelected;

    internal event Action<ActivityDay>? DateInspected;

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
        var noActivity = Colors.Transparent;
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
            var labelDate = first < this.firstSelectableDate ? this.firstSelectableDate : first;
            if (labelDate.Year == this.firstSelectableDate.Year &&
                labelDate.Month != lastMonth &&
                (week == 0 || labelDate.Day <= 7))
            {
                var label = labelDate.ToDateTime(TimeOnly.MinValue).ToString("MMM", UiCulture);
                this.DrawText(drawingContext, label, muted, 10.5, new Point(layout.Left + (week * layout.Step), 0), dpi);
                lastMonth = labelDate.Month;
            }
        }

        foreach (var day in this.days)
        {
            var rect = this.CellRect(day.Date, layout);
            if (rect is null)
            {
                continue;
            }

            var fill = day.Date < this.firstSelectableDate || day.Date > this.lastSelectableDate
                ? unavailable
                : day.HasCoverage
                    ? day.Value <= 0.001
                        ? noActivity
                        : IntensityColor(day.Intensity, this.isDark)
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

        if (this.hoverDate is { } hover && this.CellRect(hover, layout) is { } hoverRect)
        {
            var hoverOpacity = Math.Clamp(this.HoverOpacity, 0, 1);
            drawingContext.DrawRoundedRectangle(
                null,
                new Pen(
                    new SolidColorBrush(Color.FromArgb(
                        (byte)Math.Round(230 * hoverOpacity),
                        this.accent.R,
                        this.accent.G,
                        this.accent.B)),
                    1 + (0.75 * hoverOpacity)),
                new Rect(hoverRect.X - 1.5, hoverRect.Y - 1.5, hoverRect.Width + 3, hoverRect.Height + 3),
                4,
                4);
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

        if (date is null)
        {
            this.FadeHoverOut();
            return;
        }

        this.hoverDate = date;
        this.AnimateHoverIn();
        if (this.daysByDate.TryGetValue(date.Value, out var day))
        {
            this.DateInspected?.Invoke(day);
            AutomationProperties.SetItemStatus(this, this.AccessibleDescription(day));
        }

        this.InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        this.FadeHoverOut();
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
            Key.Home => this.firstSelectableDate,
            Key.End => this.lastSelectableDate,
            _ => this.selectedDate,
        };
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            if (this.CanSelect(next))
            {
                this.selectedDate = next;
                this.hoverDate = next;
                if (this.daysByDate.TryGetValue(next, out var day))
                {
                    this.DateInspected?.Invoke(day);
                    AutomationProperties.SetItemStatus(this, this.AccessibleDescription(day));
                }

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

    private void AnimateHoverIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            this.HoverOpacity = 1;
            return;
        }

        this.BeginAnimation(HoverOpacityProperty, null);
        this.HoverOpacity = 0;
        this.BeginAnimation(
            HoverOpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void FadeHoverOut()
    {
        if (this.hoverDate is not { } fadingDate)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            this.hoverDate = null;
            this.HoverOpacity = 0;
            this.InvalidateVisual();
            return;
        }

        var fade = new DoubleAnimation(this.HoverOpacity, 0, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (this.hoverDate == fadingDate && !this.IsMouseOver)
            {
                this.hoverDate = null;
                this.InvalidateVisual();
            }
        };
        this.BeginAnimation(HoverOpacityProperty, fade);
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

        var date = layout.Start.AddDays((week * 7) + row);
        return this.CanSelect(date) ? date : null;
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
        return new Rect(layout.Left + (week * layout.Step), layout.Top + (row * layout.Step), layout.CellSize, layout.CellSize);
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

    private string AccessibleDescription(ActivityDay day)
    {
        var week = this.days.Where(item => item.Date >= PlanStatisticsProjection.WeekStart(day.Date) &&
            item.Date <= PlanStatisticsProjection.WeekStart(day.Date).AddDays(6));
        var weekTotal = week.Sum(item => item.Value);
        if (!day.HasCoverage)
        {
            return $"{day.Date.ToString("dddd, MMMM d", UiCulture)}. No observation data. Week total {this.valueFormatter(weekTotal)}.";
        }

        return $"{day.Date.ToString("dddd, MMMM d", UiCulture)}. " +
            $"{this.valueFormatter(day.Value)}. " +
            $"{day.ActiveHours} active hours, {day.ObservationCount} observations. " +
            $"Fixed intensity {day.Intensity} of 4. Week total {this.valueFormatter(weekTotal)}.";
    }

    private bool CanSelect(DateOnly date) => date >= this.firstSelectableDate &&
        date <= this.lastSelectableDate &&
        this.daysByDate.ContainsKey(date);

    private static Color IntensityColor(int level, bool isDark)
    {
        var alpha = level switch
        {
            1 => 64,
            2 => 118,
            3 => 184,
            _ => 255,
        };
        var ink = isDark ? Color.FromRgb(238, 240, 244) : Color.FromRgb(18, 20, 23);
        return Color.FromArgb((byte)alpha, ink.R, ink.G, ink.B);
    }

    private void DrawText(DrawingContext context, string text, Color color, double size, Point origin, double dpi)
    {
        var formatted = new FormattedText(
            text,
            UiCulture,
            FlowDirection.LeftToRight,
            new Typeface(CalendarFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            dpi);
        context.DrawText(formatted, origin);
    }

    private sealed record CalendarLayout(DateOnly Start, int Weeks, double Left, double Top, double CellSize, double Step);
}
