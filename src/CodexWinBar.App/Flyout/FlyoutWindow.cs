using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CodexWinBar.App.Assets;
using System.Windows.Threading;
using CodexWinBar.App.Interop;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using CodexWinBar.Providers;
using Microsoft.Win32;
using DrawingRectangle = System.Drawing.Rectangle;

namespace CodexWinBar.App.Flyout;

/// <summary>Borderless taskbar-anchored provider usage flyout.</summary>
public sealed class FlyoutWindow : Window
{
    private const double FlyoutWidthDip = 348;
    private const double GapPhysicalPx = 8;
    private const int LowLevelMouseHook = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const double ShadowMarginDip = 16;
    private static readonly TimeSpan OpenDismissGrace = TimeSpan.FromMilliseconds(500);
    private static readonly Duration OpenSlideDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration OpenFadeDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration CloseSlideDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration CloseFadeDuration = new(TimeSpan.FromMilliseconds(140));

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private readonly IUsageStore store;
    private readonly UiSettingsStore uiStore;
    private readonly Action openSettings;
    private readonly Action quit;
    private readonly Action<string>? log;
    private readonly Dictionary<ProviderId, ProviderDescriptor> descriptors;
    private readonly StackPanel stack = new() { Orientation = Orientation.Vertical };
    private readonly Border root;
    private readonly TranslateTransform rootTranslate = new();
    private readonly DispatcherTimer countdownTimer;
    private readonly DispatcherTimer spinnerTimer;
    private readonly RotateTransform refreshRotate = new();
    private readonly LowLevelMouseProc mouseProc;
    private HwndSource? source;
    private IntPtr hookHandle;
    private DateTimeOffset openedAt;
    private UiSettings settings = new();
    private bool isDark;
    private bool isRefreshingUi;
    private bool wasActivated;
    private bool isClosing;
    private int animationGeneration;
    private ProviderId? currentFocusProvider;

    /// <summary>Creates a flyout bound to the usage store and app actions.</summary>
    public FlyoutWindow(IUsageStore store, UiSettingsStore uiStore, Action openSettings, Action quit, Action<string>? log = null)
    {
        this.store = store;
        this.uiStore = uiStore;
        this.openSettings = openSettings;
        this.quit = quit;
        this.log = log;
        this.descriptors = ProviderCatalog.CreateAll().ToDictionary(descriptor => descriptor.Id);
        this.mouseProc = this.HandleMouseHook;

        this.WindowStyle = WindowStyle.None;
        this.ResizeMode = ResizeMode.NoResize;
        this.ShowInTaskbar = false;
        this.Topmost = true;
        this.ShowActivated = true;
        this.AllowsTransparency = true;
        this.SizeToContent = SizeToContent.Height;
        this.Width = FlyoutWidthDip + (ShadowMarginDip * 2);
        this.Background = Brushes.Transparent;
        this.Focusable = true;
        this.root = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(ShadowMarginDip),
            Child = this.stack,
            RenderTransform = this.rootTranslate,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 0,
                Opacity = 0.28,
                Color = Colors.Black,
            },
        };
        this.root.SetResourceReference(Border.BackgroundProperty, "FlyoutPanelBackground");
        this.root.SetResourceReference(Border.BorderBrushProperty, "FlyoutSubtleBorder");
        this.Content = this.root;

        this.countdownTimer = new DispatcherTimer(DispatcherPriority.Background, this.Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        this.countdownTimer.Tick += (_, _) => this.RebuildCards();
        this.spinnerTimer = new DispatcherTimer(DispatcherPriority.Render, this.Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        this.spinnerTimer.Tick += (_, _) => this.refreshRotate.Angle = (this.refreshRotate.Angle + 18) % 360;

        this.SourceInitialized += this.OnSourceInitialized;
        this.Activated += (_, _) => this.wasActivated = true;
        this.Deactivated += (_, _) => this.HandleDeactivated();
        this.KeyDown += this.OnKeyDown;
        this.store.StateChanged += this.OnStoreStateChanged;
        SystemEvents.DisplaySettingsChanged += this.OnDisplaySettingsChanged;
    }

    /// <summary>Gets whether the flyout is currently visible.</summary>
    public bool IsOpen => this.IsVisible && !this.isClosing;

    /// <summary>Toggles the flyout, anchored to a widget rectangle expressed in physical screen pixels.</summary>
    public void Toggle(DrawingRectangle anchorPhysicalPx, ProviderId? focusProvider = null)
    {
        try
        {
            if (this.IsOpen)
            {
                if (this.currentFocusProvider == focusProvider)
                {
                    this.HideFlyout("close: toggle");
                    return;
                }

                this.log?.Invoke($"toggle-switch at {anchorPhysicalPx}; focus={focusProvider?.ConfigId() ?? "all"}");
                this.currentFocusProvider = focusProvider;
                this.RebuildCards();
                this.UpdateLayout();
                this.PlacePhysical(anchorPhysicalPx);
                this.openedAt = DateTimeOffset.UtcNow;
                return;
            }

            this.log?.Invoke($"toggle-open at {anchorPhysicalPx}; focus={focusProvider?.ConfigId() ?? "all"}");
            this.isClosing = false;
            this.currentFocusProvider = focusProvider;
            this.animationGeneration++;
            this.settings = this.uiStore.Load();
            this.isDark = !ReadSystemUsesLightTheme();
            this.log?.Invoke($"flyout theme: dark={this.isDark}");
            this.ApplyThemeResources(this.isDark);
            this.RebuildCards();

            _ = new WindowInteropHelper(this).EnsureHandle();
            this.UpdateLayout();
            this.Measure(new Size(this.Width, double.PositiveInfinity));
            this.UpdateLayout();
            var placement = this.ComputePlacement(anchorPhysicalPx, Math.Max(this.DesiredSize.Height, 80));
            this.Left = placement.X;
            this.Top = placement.Y;

            this.wasActivated = false;
            this.openedAt = DateTimeOffset.UtcNow;
            var wasVisible = this.IsVisible;
            if (!wasVisible)
            {
                this.rootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                this.root.BeginAnimation(UIElement.OpacityProperty, null);
                this.rootTranslate.Y = this.AnimationDistanceDip();
                this.root.Opacity = 0;
                this.Show();
            }

            this.UpdateLayout();
            this.PlacePhysical(anchorPhysicalPx);
            this.BeginOpenAnimation();
            if (!this.Activate())
            {
                this.log?.Invoke("toggle-open: Activate returned false");
            }

            this.Focus();
            this.store.NotifyFlyoutOpened();
            this.InstallMouseHook();
            this.countdownTimer.Start();
        }
        catch (Exception ex)
        {
            this.log?.Invoke($"toggle exception: {ex}");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // PresentationSource.FromVisual is still null when the handle is created via EnsureHandle();
        // HwndSource.FromHwnd resolves the source that already owns the fresh HWND.
        this.source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (this.source is null)
        {
            this.log?.Invoke("flyout: HwndSource unavailable at SourceInitialized");
            return;
        }

        this.source.AddHook(this.WndProc);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            this.HideFlyout("close: esc");
        }
    }

    private void OnStoreStateChanged() => _ = this.Dispatcher.BeginInvoke(() =>
    {
        if (this.IsOpen)
        {
            this.RebuildCards();
        }
    });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _ = this.Dispatcher.BeginInvoke(() =>
    {
        if (this.IsOpen)
        {
            this.HideFlyout("close: display-change");
        }
    });

    private void HandleDeactivated()
    {
        if (this.IsInOpenGrace())
        {
            return;
        }

        if (this.wasActivated || DateTimeOffset.UtcNow - this.openedAt >= OpenDismissGrace)
        {
            this.HideFlyout("close: deactivated");
        }
    }

    private bool IsInOpenGrace() => DateTimeOffset.UtcNow - this.openedAt < OpenDismissGrace;

    private void HideFlyout(string reason)
    {
        if (!this.IsVisible || this.isClosing)
        {
            return;
        }

        this.log?.Invoke(reason);
        this.isClosing = true;
        this.countdownTimer.Stop();
        this.spinnerTimer.Stop();
        var generation = ++this.animationGeneration;
        this.BeginCloseAnimation(generation);
    }

    private void BeginOpenAnimation()
    {
        var generation = this.animationGeneration;
        var distance = this.AnimationDistanceDip();
        if (this.rootTranslate.Y <= 0 || this.rootTranslate.Y > distance)
        {
            this.rootTranslate.Y = distance;
        }

        this.rootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = this.rootTranslate.Y,
            To = 0,
            Duration = OpenSlideDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
        this.root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = this.root.Opacity,
            To = 1,
            Duration = OpenFadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
        this.log?.Invoke($"flyout open animation generation {generation}");
    }

    private void BeginCloseAnimation(int generation)
    {
        var distance = this.AnimationDistanceDip();
        var slide = new DoubleAnimation
        {
            From = this.rootTranslate.Y,
            To = distance,
            Duration = CloseSlideDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
        slide.Completed += (_, _) => this.CompleteHide(generation);
        this.rootTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        this.root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = this.root.Opacity,
            To = 0,
            Duration = CloseFadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private void CompleteHide(int generation)
    {
        if (generation != this.animationGeneration || !this.isClosing)
        {
            return;
        }

        this.UninstallMouseHook();
        this.Hide();
        this.rootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        this.root.BeginAnimation(UIElement.OpacityProperty, null);
        this.rootTranslate.Y = this.AnimationDistanceDip();
        this.root.Opacity = 0;
        this.isClosing = false;
        this.currentFocusProvider = null;
    }

    private double AnimationDistanceDip() => Math.Max(1, this.root.ActualHeight);

    private void RebuildCards()
    {
        this.stack.Children.Clear();
        IReadOnlyList<ProviderState> states = this.currentFocusProvider is { } focusProvider
            ? this.store.States.Where(state => state.Provider == focusProvider).ToArray()
            : this.store.States;
        if (states.Count == 0)
        {
            this.stack.Children.Add(this.CreateEmptyCard());
        }
        else
        {
            foreach (var state in states)
            {
                this.stack.Children.Add(this.CreateProviderCard(state));
            }
        }

        this.stack.Children.Add(this.CreateFooter());
        this.UpdateRefreshSpinner(states.Any(static state => state.IsRefreshing));
    }

    private UIElement CreateProviderCard(ProviderState state)
    {
        var descriptor = this.DescriptorFor(state.Provider);
        var brand = BrandBrush(descriptor);
        var card = this.CreateCardShell();
        var content = new StackPanel { Orientation = Orientation.Vertical };
        card.Child = content;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(this.CreateProviderGlyph(descriptor, brand, 16));

        var title = new TextBlock
        {
            Text = descriptor.Metadata.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.ResourceBrush("FlyoutForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = this.SubtitleFor(state),
            FontSize = 11,
            Foreground = state.IsStale ? this.ResourceBrush("FlyoutError") : this.ResourceBrush("FlyoutSecondaryForeground"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
        };
        Grid.SetColumn(subtitle, 2);
        header.Children.Add(subtitle);
        content.Children.Add(header);

        if (state.Snapshot is { } snapshot)
        {
            this.AddUsageRows(content, descriptor, snapshot, brand);
            if (snapshot.Credits is { } credits)
            {
                content.Children.Add(this.CreateCreditsRow(credits));
            }
        }

        if (state.ServiceStatus is { IsIssue: true } status)
        {
            content.Children.Add(this.CreateIncidentRow(status));
        }

        return card;
    }

    private void AddUsageRows(StackPanel content, ProviderDescriptor descriptor, UsageSnapshot snapshot, Brush brand)
    {
        if (snapshot.Primary is { } primary)
        {
            content.Children.Add(this.CreateUsageRow(string.IsNullOrWhiteSpace(descriptor.Metadata.SessionLabel) ? "Session" : descriptor.Metadata.SessionLabel, primary, brand));
        }

        if (snapshot.Secondary is { } secondary)
        {
            content.Children.Add(this.CreateUsageRow(string.IsNullOrWhiteSpace(descriptor.Metadata.WeeklyLabel) ? "Weekly" : descriptor.Metadata.WeeklyLabel, secondary, brand));
        }

        if (snapshot.Tertiary is { } tertiary)
        {
            content.Children.Add(this.CreateUsageRow("Tertiary", tertiary, brand));
        }

        foreach (var extra in snapshot.ExtraWindows)
        {
            content.Children.Add(this.CreateUsageRow(extra.Title, extra.Window, brand));
        }
    }

    private UIElement CreateUsageRow(string label, RateWindow window, Brush brand)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = this.ResourceBrush("FlyoutForeground"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var percent = this.settings.UsageBarsShowUsed ? window.UsedPercent : window.RemainingPercent;
        var track = new Grid { Height = 6, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Background = this.ResourceBrush("FlyoutTrack") };
        track.Clip = new RectangleGeometry(new Rect(0, 0, 1, 1), 3, 3);
        track.SizeChanged += (_, _) => track.Clip = new RectangleGeometry(new Rect(0, 0, track.ActualWidth, 6), 3, 3);
        var fill = new Border { Height = 6, HorizontalAlignment = HorizontalAlignment.Left, Background = brand, CornerRadius = new CornerRadius(3) };
        fill.Width = Math.Max(0, Math.Min(100, percent)) / 100 * 160;
        track.SizeChanged += (_, _) => fill.Width = Math.Max(0, Math.Min(100, percent)) / 100 * track.ActualWidth;
        track.Children.Add(fill);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var value = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right };
        value.Children.Add(new TextBlock { Text = string.Create(CultureInfo.InvariantCulture, $"{percent:0}%"), FontSize = 12, Foreground = this.ResourceBrush("FlyoutForeground"), TextAlignment = TextAlignment.Right });
        value.Children.Add(new TextBlock { Text = this.ResetText(window), FontSize = 11, Foreground = this.ResourceBrush("FlyoutSecondaryForeground"), TextAlignment = TextAlignment.Right });
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private UIElement CreateCreditsRow(CreditsSnapshot credits)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Credits: {credits.Remaining:0.##} {credits.Unit}");
        if (credits.Limit is { } limit)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" of {limit:0.##}");
        }

        return new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 0), FontSize = 11, Foreground = this.ResourceBrush("FlyoutSecondaryForeground") };
    }

    private UIElement CreateIncidentRow(ProviderStatus status)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = status.Indicator is StatusIndicator.Minor or StatusIndicator.Maintenance ? new SolidColorBrush(Color.FromRgb(0xF8, 0xA8, 0x00)) : new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator.ToString() : status.Description,
            FontSize = 11,
            Foreground = this.ResourceBrush("FlyoutSecondaryForeground"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 284,
        });
        return row;
    }

    private UIElement CreateEmptyCard()
    {
        var card = this.CreateCardShell();
        card.Child = new TextBlock { Text = "No providers enabled - open Settings", FontSize = 13, Foreground = this.ResourceBrush("FlyoutSecondaryForeground") };
        return card;
    }

    private Border CreateCardShell() => new()
    {
        Padding = new Thickness(12),
        Margin = new Thickness(0, 0, 0, 8),
        Background = this.ResourceBrush("FlyoutCardBackground"),
        CornerRadius = new CornerRadius(8),
    };

    private FrameworkElement CreateProviderGlyph(ProviderDescriptor descriptor, Brush brand, double size)
    {
        if (LogoImages.Get(descriptor.Branding.GlyphKey, this.isDark) is { } source)
        {
            return new Image
            {
                Width = size,
                Height = size,
                Source = source,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = brand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = InitialFor(descriptor),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private UIElement CreateFooter()
    {
        var footer = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var refresh = this.CreateIconButton(this.CreateRefreshGlyph(), "Refresh");
        refresh.Click += async (_, _) => await this.RefreshAllAsync();
        footer.Children.Add(refresh);
        var settingsButton = this.CreateIconButton(LogoImages.IconGlyph(LogoImages.SettingsGlyph, 13), "Settings");
        settingsButton.Click += (_, _) => { this.HideFlyout("close: settings"); this.openSettings(); };
        Grid.SetColumn(settingsButton, 2);
        footer.Children.Add(settingsButton);
        var quitButton = this.CreateIconButton(LogoImages.IconGlyph(LogoImages.CloseGlyph, 10), "Quit");
        quitButton.Click += (_, _) => this.quit();
        Grid.SetColumn(quitButton, 3);
        footer.Children.Add(quitButton);
        return footer;
    }

    private FrameworkElement CreateRefreshGlyph()
    {
        var glyph = LogoImages.IconGlyph(LogoImages.RefreshGlyph, 13);
        glyph.RenderTransform = this.refreshRotate;
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        return glyph;
    }

    private Button CreateIconButton(object content, string label)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            Content = content,
            ToolTip = label,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = this.ResourceBrush("FlyoutForeground"),
            Focusable = true,
        };
        button.Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;
        button.MouseEnter += (_, _) => button.Background = this.ResourceBrush("FlyoutHover");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            await this.store.RefreshAllAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateRefreshSpinner(bool isRefreshing)
    {
        if (this.isRefreshingUi == isRefreshing)
        {
            return;
        }

        this.isRefreshingUi = isRefreshing;
        if (isRefreshing)
        {
            this.spinnerTimer.Start();
        }
        else
        {
            this.spinnerTimer.Stop();
            this.refreshRotate.Angle = 0;
        }
    }

    private string SubtitleFor(ProviderState state)
    {
        if (state.NeedsAuthentication)
        {
            return "signed out - open Settings";
        }

        if (state.IsStale)
        {
            return string.IsNullOrWhiteSpace(state.Error) ? "last fetch failed" : state.Error;
        }

        var identity = state.Snapshot?.Identity;
        return FirstNonEmpty(identity?.Plan, identity?.AccountEmail, identity?.AccountOrganization, identity?.LoginMethod) ?? "usage";
    }

    private string ResetText(RateWindow window)
    {
        if (window.ResetsAt is not { } resetsAt)
        {
            return window.ResetDescription ?? string.Empty;
        }

        if (this.settings.ResetTimesShowAbsolute)
        {
            return resetsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        }

        var remaining = resetsAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "resets now";
        }

        if (remaining.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"resets in {(int)remaining.TotalDays}d {remaining.Hours}h");
        }

        if (remaining.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"resets in {(int)remaining.TotalHours}h {remaining.Minutes}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m");
    }

    private Point ComputePlacement(DrawingRectangle anchorPhysicalPx, double heightDip)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var monitor = MonitorFromRect(ref anchorPhysicalPx, MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        _ = GetMonitorInfo(monitor, ref info);
        var work = info.Work;
        var presentationSource = this.source ?? (HwndSource?)PresentationSource.FromVisual(this);
        var transform = presentationSource?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var widthPx = this.Width / Math.Max(transform.M11, 0.01);
        var heightPx = heightDip / Math.Max(transform.M22, 0.01);
        var shadowMarginXPx = ShadowMarginDip / Math.Max(transform.M11, 0.01);
        var shadowMarginYPx = ShadowMarginDip / Math.Max(transform.M22, 0.01);
        var panelWidthPx = widthPx - (shadowMarginXPx * 2);
        var panelHeightPx = heightPx - (shadowMarginYPx * 2);
        var panelX = anchorPhysicalPx.Right - panelWidthPx;
        var panelY = anchorPhysicalPx.Top - panelHeightPx - GapPhysicalPx;
        panelX = Math.Max(work.Left, Math.Min(panelX, work.Right - panelWidthPx));
        panelY = Math.Max(work.Top, Math.Min(panelY, work.Bottom - panelHeightPx));
        var x = panelX - shadowMarginXPx;
        var y = panelY - shadowMarginYPx;
        x = Math.Max(work.Left, Math.Min(x, work.Right - widthPx));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - heightPx));
        _ = SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        return transform.Transform(new Point(x, y));
    }

    /// <summary>
    /// Repositions the SHOWN window purely in physical pixels via SetWindowPos, sidestepping WPF's
    /// pre-show DIP Left/Top placement, which is unreliable under per-monitor DPI.
    /// </summary>
    private void PlacePhysical(DrawingRectangle anchorPhysicalPx)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr))
        {
            return;
        }

        var widthPx = wr.Right - wr.Left;
        var heightPx = wr.Bottom - wr.Top;
        var monitor = MonitorFromRect(ref anchorPhysicalPx, MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        _ = GetMonitorInfo(monitor, ref info);
        var work = info.Work;
        var transform = (this.source ?? (HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var shadowMarginXPx = (int)Math.Round(ShadowMarginDip / Math.Max(transform.M11, 0.01));
        var shadowMarginYPx = (int)Math.Round(ShadowMarginDip / Math.Max(transform.M22, 0.01));
        var panelWidthPx = Math.Max(1, widthPx - (shadowMarginXPx * 2));
        var panelHeightPx = Math.Max(1, heightPx - (shadowMarginYPx * 2));
        var panelX = (int)Math.Max(work.Left, Math.Min(anchorPhysicalPx.Right - panelWidthPx, work.Right - panelWidthPx));
        var panelY = (int)Math.Max(work.Top, Math.Min(anchorPhysicalPx.Top - panelHeightPx - GapPhysicalPx, work.Bottom - panelHeightPx));
        var x = (int)Math.Max(work.Left, Math.Min(panelX - shadowMarginXPx, work.Right - widthPx));
        var y = (int)Math.Max(work.Top, Math.Min(panelY - shadowMarginYPx, work.Bottom - heightPx));
        _ = SetWindowPos(hwnd, HwndTop, x, y, 0, 0, SwpNoSize | SwpNoActivate);
        this.log?.Invoke($"flyout placed at ({x},{y}) size {widthPx}x{heightPx}; panel at ({x + shadowMarginXPx},{y + shadowMarginYPx}) size {panelWidthPx}x{panelHeightPx} (physical)");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDisplayChange || (msg is WmDpiChanged && this.IsOpen))
        {
            this.HideFlyout("close: display-change");
        }

        return IntPtr.Zero;
    }

    private void InstallMouseHook()
    {
        this.UninstallMouseHook();
        this.hookHandle = SetWindowsHookEx(LowLevelMouseHook, this.mouseProc, GetModuleHandle(null), 0);
        if (this.hookHandle == IntPtr.Zero)
        {
            this.log?.Invoke($"flyout: mouse hook install FAILED (error {Marshal.GetLastWin32Error()})");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private void UninstallMouseHook()
    {
        if (this.hookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(this.hookHandle);
            this.hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HandleMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && this.IsOpen && IsMouseDownMessage(wParam.ToInt32()) && !this.IsInOpenGrace())
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var windowRect = this.WindowPhysicalRect();
            if (!windowRect.Contains(hook.Point.X, hook.Point.Y))
            {
                _ = this.Dispatcher.BeginInvoke(() => this.HideFlyout("close: outside-click"));
            }
        }

        return CallNextHookEx(this.hookHandle, code, wParam, lParam);
    }

    private DrawingRectangle WindowPhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return GetWindowRect(hwnd, out var rect) ? DrawingRectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : DrawingRectangle.Empty;
    }

    private void ApplyThemeResources(bool dark)
    {
        var foreground = dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1B, 0x1B, 0x1B);
        var dictionary = new ResourceDictionary
        {
            ["FlyoutForeground"] = new SolidColorBrush(foreground),
            ["FlyoutSecondaryForeground"] = new SolidColorBrush(Color.FromArgb(0x99, foreground.R, foreground.G, foreground.B)),
            ["FlyoutTrack"] = new SolidColorBrush(Color.FromArgb(0x26, foreground.R, foreground.G, foreground.B)),
            ["FlyoutHover"] = new SolidColorBrush(Color.FromArgb(0x14, foreground.R, foreground.G, foreground.B)),
            ["FlyoutPanelBackground"] = new SolidColorBrush(dark ? Color.FromArgb(0xFA, 0x2A, 0x2A, 0x2A) : Color.FromArgb(0xFA, 0xF3, 0xF3, 0xF3)),
            ["FlyoutSubtleBorder"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x30 : (byte)0x24, foreground.R, foreground.G, foreground.B)),
            ["FlyoutCardBackground"] = new SolidColorBrush(dark ? Color.FromArgb(0x59, 0x3A, 0x3A, 0x3A) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            ["FlyoutError"] = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
        };
        this.Resources.MergedDictionaries.Clear();
        this.Resources.MergedDictionaries.Add(dictionary);
    }

    private Brush ResourceBrush(string key) => (Brush)this.Resources[key];

    private ProviderDescriptor DescriptorFor(ProviderId provider) => this.descriptors.TryGetValue(provider, out var descriptor)
        ? descriptor
        : new ProviderDescriptor
        {
            Id = provider,
            Metadata = new ProviderMetadata { DisplayName = provider.ToString() },
            Branding = new ProviderBranding { GlyphKey = provider.ToString(), R = 0x66, G = 0x66, B = 0x66 },
            Strategies = [],
        };

    private static Brush BrandBrush(ProviderDescriptor descriptor) => new SolidColorBrush(Color.FromRgb(descriptor.Branding.R, descriptor.Branding.G, descriptor.Branding.B));

    private static string InitialFor(ProviderDescriptor descriptor)
    {
        var name = descriptor.Metadata.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString(CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsMouseDownMessage(int message) => message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private static bool ReadSystemUsesLightTheme()
    {
        const string personalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(personalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is int value ? value != 0 : true;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromRect(ref DrawingRectangle lprc, int dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>() };
    }
}
