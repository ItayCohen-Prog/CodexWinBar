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
    private readonly Color activityInk;
    private readonly bool isDark;
    private readonly Func<double, string> valueFormatter;
    private readonly DateOnly firstSelectableDate;
    private readonly DateOnly lastSelectableDate;
    private DateOnly selectedDate;
    private DateOnly? hoverDate;
    private bool isFadingHover;

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
        Color activityInk,
        bool isDark,
        Func<double, string> valueFormatter)
    {
        this.days = days;
        this.daysByDate = days.ToDictionary(day => day.Date);
        this.selectedDate = selectedDate;
        this.firstSelectableDate = firstSelectableDate;
        this.lastSelectableDate = lastSelectableDate;
        this.activityInk = activityInk;
        this.isDark = isDark;
        this.valueFormatter = valueFormatter;
        this.Focusable = true;
        this.Cursor = Cursors.Hand;
        this.MinHeight = 148;
        AutomationProperties.SetName(this, "Observed allowance activity calendar");
        AutomationProperties.SetHelpText(this, "Use arrow keys to inspect days, Home or End to jump, then press Enter to open the selected month.");
    }

    internal event Action<DateOnly>? DateSelected;

    internal event Action<bool>? MonthHoverChanged;

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
                        : IntensityColor(day.Intensity, this.activityInk)
                    : unavailable;
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect.Value, 2.5, 2.5);
        }

        if (this.hoverDate is { } hover)
        {
            var hoverOpacity = Math.Clamp(this.HoverOpacity, 0, 1);
            var outlinePen = new Pen(
                new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(210 * hoverOpacity),
                    this.activityInk.R,
                    this.activityInk.G,
                    this.activityInk.B)),
                1.4)
            {
                LineJoin = PenLineJoin.Round,
            };
            drawingContext.DrawGeometry(null, outlinePen, this.MonthGeometry(hover, layout));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var date = this.DateAt(e.GetPosition(this));
        if (date is null)
        {
            this.FadeHoverOut();
            return;
        }

        if (this.hoverDate is { } previous && IsSameCalendarMonth(previous, date.Value))
        {
            this.hoverDate = date;
            if (this.isFadingHover)
            {
                this.AnimateHoverIn(fromCurrentOpacity: true);
                this.MonthHoverChanged?.Invoke(true);
            }

            if (this.daysByDate.TryGetValue(date.Value, out var hoveredDay))
            {
                AutomationProperties.SetItemStatus(this, this.AccessibleDescription(hoveredDay));
            }

            return;
        }

        this.hoverDate = date;
        this.AnimateHoverIn(fromCurrentOpacity: false);
        this.MonthHoverChanged?.Invoke(true);

        if (this.daysByDate.TryGetValue(date.Value, out var day))
        {
            AutomationProperties.SetItemStatus(this, this.AccessibleDescription(day));
        }

        this.InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        this.FadeHoverOut();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Tabbing in outlines the month Enter would open. A pointer click focuses too, but its
        // hover outline is already in place and the click is about to change the view anyway.
        if (!this.IsMouseOver && this.CanSelect(this.selectedDate))
        {
            this.ShowKeyboardHover(this.selectedDate);
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!this.IsMouseOver)
        {
            this.FadeHoverOut();
        }
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
                this.ShowKeyboardHover(next);
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

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var layout = this.Layout();
        var halfGap = (layout.Step - layout.CellSize) / 2;
        var gridBounds = new Rect(
            layout.Left - halfGap,
            layout.Top - halfGap,
            layout.Weeks * layout.Step,
            7 * layout.Step);
        return gridBounds.Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : base.HitTestCore(hitTestParameters);
    }

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

    // Keyboard travel drives the same month outline and header cue as the pointer; without the
    // opacity animation the outline pen would stay fully transparent and arrow keys would move an
    // invisible selection.
    private void ShowKeyboardHover(DateOnly date)
    {
        this.hoverDate = date;
        this.AnimateHoverIn(fromCurrentOpacity: true);
        this.MonthHoverChanged?.Invoke(true);
        if (this.daysByDate.TryGetValue(date, out var day))
        {
            AutomationProperties.SetItemStatus(this, this.AccessibleDescription(day));
        }

        this.InvalidateVisual();
    }

    private void AnimateHoverIn(bool fromCurrentOpacity = false)
    {
        this.isFadingHover = false;
        if (!SystemParameters.ClientAreaAnimation)
        {
            this.HoverOpacity = 1;
            return;
        }

        var startOpacity = fromCurrentOpacity ? this.HoverOpacity : 0;
        this.BeginAnimation(HoverOpacityProperty, null);
        this.HoverOpacity = startOpacity;
        this.BeginAnimation(
            HoverOpacityProperty,
            new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(110))
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

        if (this.isFadingHover)
        {
            return;
        }

        this.MonthHoverChanged?.Invoke(false);

        if (!SystemParameters.ClientAreaAnimation)
        {
            this.isFadingHover = false;
            this.hoverDate = null;
            this.HoverOpacity = 0;
            this.InvalidateVisual();
            return;
        }

        this.isFadingHover = true;
        var fade = new DoubleAnimation(this.HoverOpacity, 0, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (this.isFadingHover && this.hoverDate == fadingDate)
            {
                this.isFadingHover = false;
                this.hoverDate = null;
                this.InvalidateVisual();
            }
        };
        this.BeginAnimation(HoverOpacityProperty, fade);
    }

    private DateOnly? DateAt(Point point)
    {
        var layout = this.Layout();
        var halfGap = (layout.Step - layout.CellSize) / 2;
        var week = (int)Math.Floor((point.X - layout.Left + halfGap) / layout.Step);
        var row = (int)Math.Floor((point.Y - layout.Top + halfGap) / layout.Step);
        if (week < 0 || week >= layout.Weeks || row < 0 || row >= 7)
        {
            return null;
        }

        var date = layout.Start.AddDays((week * 7) + row);
        return this.CanSelect(date) ? date : null;
    }

    private Geometry MonthGeometry(DateOnly month, CalendarLayout layout)
    {
        Geometry? region = null;
        var overlap = ((layout.Step - layout.CellSize) / 2) + 0.25;
        foreach (var day in this.days.Where(day => IsSameCalendarMonth(day.Date, month)))
        {
            if (this.CellRect(day.Date, layout) is not { } cellRect)
            {
                continue;
            }

            cellRect.Inflate(overlap, overlap);
            var cellRegion = new RectangleGeometry(cellRect);
            region = region is null
                ? cellRegion
                : new CombinedGeometry(GeometryCombineMode.Union, region, cellRegion);
        }

        return region ?? Geometry.Empty;
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
            $"{StatisticsAccessibility.Count(day.ActiveHours, "active hour")}, {StatisticsAccessibility.Count(day.ObservationCount, "observation")}. " +
            $"Fixed intensity {day.Intensity.ToString("P0", UiCulture)}. Week total {this.valueFormatter(weekTotal)}.";
    }

    internal static bool IsSameCalendarMonth(DateOnly left, DateOnly right) =>
        left.Year == right.Year && left.Month == right.Month;

    private bool CanSelect(DateOnly date) => date >= this.firstSelectableDate &&
        date <= this.lastSelectableDate &&
        this.daysByDate.ContainsKey(date);

    // Shared with the legend swatches so the printed scale can never drift from the cells.
    internal static Color IntensityColor(double intensity, Color ink)
    {
        // Preserve the fixed 0–100% endpoints while keeping typical low daily usage distinguishable.
        var alpha = (byte)Math.Round(255 * Math.Sqrt(Math.Clamp(intensity, 0, 1)));
        return Color.FromArgb(alpha, ink.R, ink.G, ink.B);
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
