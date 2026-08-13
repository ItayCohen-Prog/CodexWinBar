using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexWinBar.App.Statistics;

internal sealed record ActivityBar(string Label, double Value, string Description, bool IsEnabled = true);

/// <summary>An accessible bar chart built from focusable WPF buttons.</summary>
internal sealed class ActivityBarChart : Grid
{
    private readonly IReadOnlyList<ActivityBar> bars;
    private readonly Color accent;
    private readonly bool isDark;
    private readonly bool interactive;

    internal ActivityBarChart(
        IReadOnlyList<ActivityBar> bars,
        Color accent,
        bool isDark,
        bool interactive)
    {
        this.bars = bars;
        this.accent = accent;
        this.isDark = isDark;
        this.interactive = interactive;
        this.MinHeight = 200;
        AutomationProperties.SetName(this, "Activity bar chart");
        this.Build();
    }

    internal event Action<int>? BarSelected;

    private void Build()
    {
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
        this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        this.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        this.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var max = Math.Max(1, this.bars.Count == 0 ? 0 : this.bars.Max(bar => bar.Value));
        var labels = new Grid();
        labels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        labels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        labels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        this.AddAxisLabel(labels, 0, $"{max:0.#}", VerticalAlignment.Top);
        this.AddAxisLabel(labels, 1, $"{max / 2:0.#}", VerticalAlignment.Center);
        this.AddAxisLabel(labels, 2, "0", VerticalAlignment.Bottom);
        this.Children.Add(labels);

        var plot = new Grid();
        for (var index = 0; index < this.bars.Count; index++)
        {
            plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (var alignment in new[] { VerticalAlignment.Top, VerticalAlignment.Center, VerticalAlignment.Bottom })
        {
            plot.Children.Add(new Border
            {
                Height = 1,
                VerticalAlignment = alignment,
                Background = new SolidColorBrush(this.isDark
                    ? Color.FromArgb(50, 255, 255, 255)
                    : Color.FromArgb(30, 23, 25, 29)),
            });
        }

        for (var index = 0; index < this.bars.Count; index++)
        {
            var bar = this.bars[index];
            var slot = new Grid { Margin = new Thickness(3, 0, 3, 0) };
            var button = new Button
            {
                Height = bar.Value <= 0 ? 3 : Math.Max(5, 154 * bar.Value / max),
                Width = this.bars.Count > 12 ? 12 : 30,
                MaxWidth = 30,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(205, this.accent.R, this.accent.G, this.accent.B)),
                BorderBrush = new SolidColorBrush(this.accent),
                BorderThickness = new Thickness(1),
                Cursor = this.interactive && bar.IsEnabled ? Cursors.Hand : Cursors.Arrow,
                IsEnabled = !this.interactive || bar.IsEnabled,
                ToolTip = bar.Description,
                Tag = index,
            };
            button.Click += (_, _) =>
            {
                if (this.interactive && bar.IsEnabled)
                {
                    this.BarSelected?.Invoke((int)button.Tag);
                }
            };
            AutomationProperties.SetName(
                button,
                this.interactive ? $"{bar.Description}. Open day." : bar.Description);
            slot.Children.Add(button);
            Grid.SetColumn(slot, index);
            plot.Children.Add(slot);
        }

        Grid.SetColumn(plot, 1);
        this.Children.Add(plot);

        var barLabels = new Grid();
        for (var index = 0; index < this.bars.Count; index++)
        {
            barLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (this.bars.Count > 8 && index % 3 != 0)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = this.bars[index].Label,
                FontSize = 10.5,
                Foreground = this.MutedBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, index);
            barLabels.Children.Add(label);
        }

        Grid.SetRow(barLabels, 1);
        Grid.SetColumn(barLabels, 1);
        this.Children.Add(barLabels);
    }

    private void AddAxisLabel(Grid labels, int row, string value, VerticalAlignment alignment)
    {
        var label = new TextBlock
        {
            Text = value,
            FontSize = 10.5,
            Foreground = this.MutedBrush(),
            VerticalAlignment = alignment,
        };
        Grid.SetRow(label, row);
        labels.Children.Add(label);
    }

    private SolidColorBrush MutedBrush() => new(
        this.isDark ? Color.FromRgb(167, 173, 184) : Color.FromRgb(98, 104, 116));
}
