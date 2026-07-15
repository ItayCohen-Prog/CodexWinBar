using System.Diagnostics;
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
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int AbeLeft = 0;
    private const int AbeTop = 1;
    private const int AbeRight = 2;
    private const int AbeBottom = 3;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const double ShadowMarginDip = 16;
    private static readonly TimeSpan OpenDismissGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StaleDataAge = TimeSpan.FromMinutes(10);
    private static readonly Duration OpenSlideDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration OpenFadeDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration CloseSlideDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration CloseFadeDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration SwitchFadeOutDuration = new(TimeSpan.FromMilliseconds(110));

    // The resize spans the whole cross-fade (fade-out + fade-in) so the box morphs continuously
    // while the old text fades out and the new text fades in — keep it their sum.
    private static readonly Duration SwitchResizeDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration SwitchFadeInDuration = new(TimeSpan.FromMilliseconds(140));

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private readonly IUsageStore store;
    private readonly UiSettingsStore uiStore;
    private readonly Action openSettings;
    private readonly Action quit;
    private readonly Action onStartUpdateDownload;
    private readonly Action onApplyUpdate;
    private readonly Action<string>? log;
    private readonly Dictionary<ProviderId, ProviderDescriptor> descriptors;
    private readonly StackPanel stack = new() { Orientation = Orientation.Vertical };
    private readonly ScrollViewer scroll;
    private readonly ContentControl footerHost = new();
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
    private bool isPreWarming;
    private UpdateStage updateStage = UpdateStage.None;
    private double updateProgress;
    private string? updateError;
    private Button? updateButton;
    private DateTimeOffset lastToggleAt;
    private int animationGeneration;

    // Generation whose provider-switch animation currently owns root.Height; while it equals
    // animationGeneration, EnsureSessionWindowFits must not measure (it would read the animated
    // height) or clear the running animation. int.MinValue = no switch in flight.
    private int switchAnimationGeneration = int.MinValue;
    private int measureNonce;
    private int currentEdge = AbeBottom;
    private ProviderId? currentFocusProvider;

    // The HWND is sized ONCE per open session to the tallest provider and never resized during a
    // switch — resizing a part-native WPF window mid-animation desyncs the native resize from WPF's
    // render composition, which is the jitter. Switches animate only the inner panel (WPF sandbox).
    private double sessionWindowHeightDip;

    // The widget anchor (physical pixels) the flyout was last placed against. Used to tell whether a
    // provider switch was clicked on the SAME monitor (in-place cross-fade) or a DIFFERENT one (the
    // flyout must move to the clicked monitor, not stay on the previous one).
    private DrawingRectangle currentAnchorPhysicalPx;

    // Set while the flyout is being deliberately relocated to another monitor. Relocating across a
    // DPI boundary fires WM_DPICHANGED, which normally closes the flyout (a user dragging it to a new
    // DPI can't be re-laid-out cleanly); this flag tells that handler to ignore the self-induced change.
    private bool suppressDpiClose;

    /// <summary>Creates a flyout bound to the usage store and app actions.</summary>
    /// <summary>Lifecycle of the footer's update button: hidden, an available update to download, a
    /// download in progress, a staged update ready to restart into, or a failed attempt.</summary>
    private enum UpdateStage { None, Available, Downloading, Ready, Error }

    public FlyoutWindow(IUsageStore store, UiSettingsStore uiStore, Action openSettings, Action quit, Action onStartUpdateDownload, Action onApplyUpdate, Action<string>? log = null)
    {
        this.store = store;
        this.uiStore = uiStore;
        this.openSettings = openSettings;
        this.quit = quit;
        this.onStartUpdateDownload = onStartUpdateDownload;
        this.onApplyUpdate = onApplyUpdate;
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
        this.UseLayoutRounding = true;
        this.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        this.scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Focusable = false,
        };
        this.root = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(ShadowMarginDip),
            Child = this.CreateFlyoutLayout(),
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
        // Bottom-anchored so the panel can grow/shrink from the top while its bottom edge stays put,
        // and so it sits at the bottom when the window is momentarily taller than the panel (switch).
        this.root.VerticalAlignment = VerticalAlignment.Bottom;
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
    public bool IsOpen => this.IsVisible && !this.isClosing && !this.isPreWarming;

    /// <summary>
    /// Renders the flyout once off-screen and invisibly at startup so WPF's composition/animation
    /// pipeline is warm — otherwise the very first real open drops its slide/fade animation. The window
    /// is non-activating during the warm-up (no focus steal, no Activated/Deactivated, no dismiss hook),
    /// and <see cref="IsOpen"/> stays false throughout so nothing treats the warm-up as an open flyout.
    /// Hide keeps the HWND alive, so the next real open reuses the warmed window.
    /// </summary>
    public void PreWarm()
    {
        if (this.IsVisible || this.isPreWarming)
        {
            return;
        }

        this.isPreWarming = true;
        this.ShowActivated = false;
        this.Opacity = 0;
        this.Left = -32000;
        this.Top = -32000;
        try
        {
            this.Show();
        }
        catch (Exception ex)
        {
            this.log?.Invoke($"flyout pre-warm failed: {ex.GetType().Name}");
            this.isPreWarming = false;
            this.ShowActivated = true;
            this.Opacity = 1;
            return;
        }

        // After one render pass the pipeline is warm; hide and restore for real opens.
        _ = this.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            this.Hide();
            this.isPreWarming = false;
            this.ShowActivated = true;
            this.Opacity = 1;
        }));
    }

    /// <summary>Toggles the flyout, anchored to a widget rectangle expressed in physical screen pixels.</summary>
    public void Toggle(DrawingRectangle anchorPhysicalPx, ProviderId? focusProvider = null)
    {
        try
        {
            // Coalesce ultra-rapid toggles. Mashing the same widget open/close faster than the
            // open/close animations settle thrashes the transparent flyout window's show/hide against
            // the (topmost, layered) overlay widget and tears — invisible when embedded and in the
            // slower Debug build, visible in Release/overlay. 150 ms is well below a deliberate
            // open-then-close, so normal use never hits it.
            var now = DateTimeOffset.UtcNow;
            if (now - this.lastToggleAt < TimeSpan.FromMilliseconds(150))
            {
                return;
            }

            this.lastToggleAt = now;

            // A null focus (tray/keyboard activation) means the combined all-providers view: every
            // provider's card stacked in one scrollable panel, capped to the monitor work area by
            // PrepareSessionWindow. A concrete focus (widget chip click) shows that provider alone.
            if (this.IsOpen)
            {
                if (this.currentFocusProvider == focusProvider)
                {
                    // Same provider on a DIFFERENT monitor: bring the flyout to the clicked monitor
                    // rather than closing — the toggle-closed gesture only applies on the monitor the
                    // flyout is actually showing on.
                    if (focusProvider is { } sameProvider && !this.AnchorOnCurrentMonitor(anchorPhysicalPx))
                    {
                        this.log?.Invoke($"toggle same-provider cross-monitor at {anchorPhysicalPx}; focus={sameProvider.ConfigId()}");
                        this.ReanchorToProvider(sameProvider, anchorPhysicalPx);
                        return;
                    }

                    this.log?.Invoke($"toggle-close at {anchorPhysicalPx}; focus={focusProvider?.ConfigId() ?? "all"}");
                    this.HideFlyout("close: toggle");
                    return;
                }

                if (focusProvider is { } provider && this.currentFocusProvider is { } currentProvider && provider != currentProvider)
                {
                    // Same monitor: the in-place cross-fade (window fixed, only the panel animates).
                    // Different monitor: the flyout must follow the click to that monitor, so re-anchor
                    // and slide in there rather than cross-fading in place on the old monitor.
                    if (this.AnchorOnCurrentMonitor(anchorPhysicalPx))
                    {
                        this.log?.Invoke($"toggle-switch at {anchorPhysicalPx}; focus={provider.ConfigId()}");
                        this.currentAnchorPhysicalPx = anchorPhysicalPx;
                        _ = this.SwitchProviderAsync(provider);
                        this.openedAt = DateTimeOffset.UtcNow;
                        return;
                    }

                    this.log?.Invoke($"toggle-switch cross-monitor at {anchorPhysicalPx}; focus={provider.ConfigId()}");
                    this.ReanchorToProvider(provider, anchorPhysicalPx);
                    return;
                }

                this.log?.Invoke($"toggle-rescope at {anchorPhysicalPx}; focus={focusProvider?.ConfigId() ?? "all"}");
                this.currentFocusProvider = focusProvider;
                this.RebuildCards();
                // Re-run session sizing: rescoping between the all-providers view and a single provider
                // changes the required height, so the window must resize (it is not animated here). On an
                // already-shown window WPF applies this.Height too late for PlacePhysical's size read, so
                // resize the HWND explicitly first.
                this.PrepareSessionWindow(anchorPhysicalPx);
                this.ResizeHwndHeight(this.sessionWindowHeightDip);
                this.UpdateLayout();
                this.PlacePhysical(anchorPhysicalPx);
                this.currentAnchorPhysicalPx = anchorPhysicalPx;
                this.openedAt = DateTimeOffset.UtcNow;
                return;
            }

            this.log?.Invoke($"toggle-open at {anchorPhysicalPx}; focus={focusProvider?.ConfigId() ?? "all"}");
            this.isClosing = false;
            this.currentEdge = QueryTaskbarEdge();
            this.currentFocusProvider = focusProvider;
            this.animationGeneration++;
            this.settings = this.uiStore.Load();
            this.isDark = !ReadSystemUsesLightTheme();
            this.log?.Invoke($"flyout theme: dark={this.isDark}");
            this.ApplyThemeResources(this.isDark);
            this.footerHost.Content = null;
            this.RebuildCards();

            _ = new WindowInteropHelper(this).EnsureHandle();
            this.UpdateLayout();

            this.PrepareSessionWindow(anchorPhysicalPx);
            this.UpdateLayout();
            var placement = this.ComputePlacement(anchorPhysicalPx, this.sessionWindowHeightDip);
            this.Left = placement.X;
            this.Top = placement.Y;

            this.wasActivated = false;
            this.openedAt = DateTimeOffset.UtcNow;
            var wasVisible = this.IsVisible;
            if (!wasVisible)
            {
                this.root.BeginAnimation(UIElement.OpacityProperty, null);
                this.ApplySlideOffset(this.SlideStartOffset());
                this.root.Opacity = 0;
                this.Show();
            }

            this.UpdateLayout();
            this.PlacePhysical(anchorPhysicalPx);
            this.currentAnchorPhysicalPx = anchorPhysicalPx;

            // Fresh show: start the slide in lockstep with the compositor's next frame instead of now.
            // A wall-clock DoubleAnimation started synchronously charges the (slowest-on-first-open)
            // first-frame realization cost against its own clock, so the first visible frame lands
            // partway through the slide — the "skipped frame" pop. Deferring to the first
            // CompositionTarget.Rendering tick ties t=0 to the actual first composited frame (the root
            // is held at opacity 0 until then, so nothing shows early). A re-open mid-close is already
            // realized, so it animates immediately from wherever it is.
            if (wasVisible)
            {
                this.BeginOpenAnimation();
            }
            else
            {
                this.BeginOpenAnimationOnNextFrame();
            }

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

    private DockPanel CreateFlyoutLayout()
    {
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(this.footerHost, Dock.Bottom);
        layout.Children.Add(this.footerHost);
        // The card list scrolls when there are too many providers to fit the screen (the footer stays
        // pinned below it). For the common short cases the ScrollViewer just reports its content size,
        // so nothing scrolls and the panel sizes naturally.
        this.scroll.Content = this.stack;
        layout.Children.Add(this.scroll);
        return layout;
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
            this.EnsureSessionWindowFits();
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
        var grace = this.IsInOpenGrace();
        var overWidget = IsCursorOverWidget();
        if (grace)
        {
            return;
        }

        // Clicking the widget deactivates the flyout; that click is a switch/toggle, not a dismiss.
        // Let the widget's Clicked -> Toggle handle it (in-place cross-fade) instead of racing it
        // with a close (which turned switches into a close+reopen slide).
        if (overWidget)
        {
            return;
        }

        if (this.wasActivated || DateTimeOffset.UtcNow - this.openedAt >= OpenDismissGrace)
        {
            this.HideFlyout("close: deactivated");
        }
    }

    private static bool IsCursorOverWidget()
    {
        return GetCursorPos(out var point) && IsWidgetClick(point);
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
        this.stack.BeginAnimation(UIElement.OpacityProperty, null);
        this.BeginCloseAnimation(generation);
    }

    /// <summary>True when <paramref name="anchorPhysicalPx"/> sits on the same monitor the flyout is
    /// currently anchored to, so a provider switch can cross-fade in place instead of relocating.</summary>
    private bool AnchorOnCurrentMonitor(DrawingRectangle anchorPhysicalPx)
    {
        var clicked = anchorPhysicalPx;
        var current = this.currentAnchorPhysicalPx;
        return MonitorFromRect(ref clicked, MonitorDefaultToNearest)
            == MonitorFromRect(ref current, MonitorDefaultToNearest);
    }

    /// <summary>
    /// Moves the already-open flyout to the monitor of <paramref name="anchorPhysicalPx"/> and slides
    /// it in for <paramref name="provider"/>. Used when a chip on a DIFFERENT monitor is clicked while
    /// the flyout is open elsewhere — the in-place cross-fade would strand it on the old monitor. The
    /// root is hidden before the window is repositioned so the cross-monitor move never shows as a jump.
    /// </summary>
    private void ReanchorToProvider(ProviderId provider, DrawingRectangle anchorPhysicalPx)
    {
        var generation = ++this.animationGeneration;   // cancel any in-flight open/switch animation
        this.isClosing = false;
        this.openedAt = DateTimeOffset.UtcNow;          // grace: the relocation itself must not self-dismiss

        // Hide the content so relocating across monitors (and the DPI rescale it triggers) isn't visible.
        this.root.BeginAnimation(UIElement.OpacityProperty, null);
        this.root.Opacity = 0;
        this.stack.BeginAnimation(UIElement.OpacityProperty, null);
        this.stack.Opacity = 1;

        this.currentEdge = QueryTaskbarEdge();
        this.currentFocusProvider = provider;
        this.RebuildCards();

        // Move to the clicked monitor. If it is a different DPI this fires WM_DPICHANGED (which would
        // otherwise close the flyout); suppress that while we own the move. WPF rescales as a result.
        this.suppressDpiClose = true;
        this.PrepareSessionWindow(anchorPhysicalPx);
        this.ResizeHwndHeight(this.sessionWindowHeightDip);
        this.UpdateLayout();
        this.PlacePhysical(anchorPhysicalPx);

        // Once WPF has applied the new monitor's DPI, re-measure/re-place so size and position are
        // correct for it, then slide in. Deferred a tick so the DPI transform is settled first.
        _ = this.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            this.suppressDpiClose = false;
            if (this.animationGeneration != generation || this.isClosing || !this.IsVisible)
            {
                return;
            }

            this.PrepareSessionWindow(anchorPhysicalPx);
            this.ResizeHwndHeight(this.sessionWindowHeightDip);
            this.UpdateLayout();
            this.PlacePhysical(anchorPhysicalPx);
            this.currentAnchorPhysicalPx = anchorPhysicalPx;

            this.ApplySlideOffset(this.SlideStartOffset());
            this.root.Opacity = 0;
            this.BeginOpenAnimation();
            this.openedAt = DateTimeOffset.UtcNow;
        }));
    }

    private async Task SwitchProviderAsync(ProviderId focusProvider)
    {
        var generation = ++this.animationGeneration;
        this.switchAnimationGeneration = generation;
        try
        {
            await this.SwitchProviderCoreAsync(focusProvider, generation).ConfigureAwait(true);
        }
        finally
        {
            // Only the still-current switch releases the guard (a superseding animation bumped the
            // generation, so the stale value can never match again). Data may have refreshed while
            // fit checks were suppressed — re-check now that root.Height is back to natural.
            if (this.animationGeneration == generation)
            {
                this.switchAnimationGeneration = int.MinValue;
                this.EnsureSessionWindowFits();
            }
        }
    }

    private async Task SwitchProviderCoreAsync(ProviderId focusProvider, int generation)
    {
        // Any slide-open translate/opacity leftovers snap to their resting values so the switch
        // works from a clean state regardless of what was mid-flight.
        this.ApplySlideOffset(0);
        this.root.BeginAnimation(UIElement.OpacityProperty, null);
        this.root.Opacity = 1;

        // Panel border-box height currently shown, and the switch target's. The target is
        // pre-measured by briefly rebuilding to the new provider and back — all synchronous within
        // this dispatcher frame, so nothing renders mid-shuffle — because the resize must START
        // TOGETHER with the fade-out. Sequencing it after (resize only alongside the fade-in) left
        // the new card sitting in the old provider's box with a slack band above the footer, then a
        // late catch-up shrink: two disjoint motions instead of one continuous morph.
        var oldPanelBox = this.root.ActualHeight > 0
            ? this.root.ActualHeight
            : Math.Max(40, this.sessionWindowHeightDip - (2 * ShadowMarginDip));
        this.root.BeginAnimation(FrameworkElement.HeightProperty, null);
        this.root.Height = double.NaN;
        var previousFocus = this.currentFocusProvider;
        this.currentFocusProvider = focusProvider;
        this.RebuildCards();
        // A REAL layout pass, not MeasureNaturalHeight: its nonce trick only varies the constraint
        // between consecutive manual measures, so a lone mid-switch measure can still hit the cached
        // previous provider's DesiredSize (seen live: Claude measured at Codex's height, making the
        // resize a From==To no-op that snapped at settle). UpdateLayout re-measures the rebuilt tree
        // with the window's own constraints; nothing renders until this method first awaits.
        this.root.UpdateLayout();
        // Clamp to the fixed session window: a taller-than-window target scrolls (or the switch's
        // finally regrows the window once settled) rather than animating past the HWND and clipping.
        var newPanelBox = Math.Clamp(
            this.root.ActualHeight,
            40,
            Math.Max(40, this.sessionWindowHeightDip - (2 * ShadowMarginDip)));
        this.currentFocusProvider = previousFocus;
        this.RebuildCards();
        this.log?.Invoke($"switch panel {oldPanelBox:F1} -> {newPanelBox:F1} dip");

        // The WINDOW is never touched here. Its HWND was fixed to the tallest provider at open, so
        // the whole switch happens INSIDE the WPF sandbox: only the bottom-anchored panel's Height
        // animates (top edge expands/contracts, bottom edge stationary) while the cards cross-fade.
        // No SetWindowPos means no native-vs-WPF resize desync — the documented cause of the jitter.
        this.root.Height = oldPanelBox;
        var resizeTask = this.AnimateRootHeightAsync(oldPanelBox, newPanelBox, generation);

        await this.AnimateCardsOpacityAsync(this.stack.Opacity, 0, SwitchFadeOutDuration, EasingMode.EaseIn, generation).ConfigureAwait(true);
        if (!this.IsCurrentAnimation(generation))
        {
            return;
        }

        this.currentFocusProvider = focusProvider;
        this.RebuildCards();
        var fadeInTask = this.AnimateCardsOpacityAsync(0, 1, SwitchFadeInDuration, EasingMode.EaseOut, generation);
        await Task.WhenAll(resizeTask, fadeInTask).ConfigureAwait(true);
        if (!this.IsCurrentAnimation(generation))
        {
            return;
        }

        // Settle: hand the panel back to natural sizing. It stays bottom-anchored in the fixed window.
        this.root.BeginAnimation(FrameworkElement.HeightProperty, null);
        this.root.Height = double.NaN;
        this.stack.Opacity = 1;
    }

    /// <summary>
    /// Sizes the HWND for the current focus's session: once to the TALLEST provider (so switching only
    /// animates the bottom-anchored panel, never the window — the jitter-free path), capped to the
    /// monitor work area so the all-providers view can't exceed the screen. When the current content is
    /// taller than the capped window it pins the panel to the full height and enables the ScrollViewer;
    /// otherwise the panel sizes naturally, sits bottom-anchored, and the scrollbar stays hidden.
    /// Called on open and on rescope so both size correctly.
    /// </summary>
    private void PrepareSessionWindow(DrawingRectangle anchorPhysicalPx)
    {
        var measuredMax = this.MeasureMaxWindowHeightDip();
        var heightCap = Math.Max(160, this.WorkAreaHeightDip(anchorPhysicalPx) - 8);
        this.sessionWindowHeightDip = Math.Min(measuredMax, heightCap);
        this.SizeToContent = SizeToContent.Manual;
        this.Height = this.sessionWindowHeightDip;

        var currentNatural = this.MeasureNaturalHeight();
        var overflow = currentNatural > this.sessionWindowHeightDip + 0.5;
        this.root.BeginAnimation(FrameworkElement.HeightProperty, null);
        this.root.Height = overflow ? this.sessionWindowHeightDip - (2 * ShadowMarginDip) : double.NaN;
        this.scroll.VerticalScrollBarVisibility = overflow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    /// <summary>
    /// Re-runs session sizing when a data refresh grows the current content past the fixed session
    /// window — e.g. a card gaining reset/credit rows once the first live fetch lands after open.
    /// Without this the card bottom clips: the HWND was sized to heights measured before those rows
    /// existed and a plain RebuildCards never resizes it. Skipped while a provider switch animates
    /// the panel height (the measurement would read the animated height, not the content's), and on
    /// no-growth refreshes so the common tick stays resize-free.
    /// </summary>
    private void EnsureSessionWindowFits()
    {
        if (!this.IsOpen || this.animationGeneration == this.switchAnimationGeneration)
        {
            return;
        }

        if (this.MeasureNaturalHeight() <= this.sessionWindowHeightDip + 0.5)
        {
            return;
        }

        this.PrepareSessionWindow(this.currentAnchorPhysicalPx);
        this.ResizeHwndHeight(this.sessionWindowHeightDip);
        this.UpdateLayout();
        this.PlacePhysical(this.currentAnchorPhysicalPx);
        this.log?.Invoke($"flyout resized to fit refreshed content: {this.sessionWindowHeightDip:F0} dip");
    }

    /// <summary>
    /// Measures the panel height (including shadow margins) for every focusable provider and returns
    /// the tallest, so the HWND can be fixed to that size for the whole open session. Rebuilds the
    /// cards for the current focus before returning.
    /// </summary>
    private double MeasureMaxWindowHeightDip()
    {
        // Release any pinned panel height (e.g. left over from an all-providers overflow session) so
        // each provider measures its NATURAL content height, not the previous explicit root.Height.
        this.root.BeginAnimation(FrameworkElement.HeightProperty, null);
        this.root.Height = double.NaN;
        var savedFocus = this.currentFocusProvider;
        // Include the current focus (which may be null = the all-providers view opened from the tray,
        // taller than any single card) alongside every single-provider switch target.
        var focuses = new List<ProviderId?> { savedFocus };
        focuses.AddRange(this.store.States.Select(state => (ProviderId?)state.Provider));
        focuses = focuses.Distinct().ToList();

        var max = 0.0;
        foreach (var focus in focuses)
        {
            this.currentFocusProvider = focus;
            this.RebuildCards();
            max = Math.Max(max, this.MeasureNaturalHeight());
        }

        this.currentFocusProvider = savedFocus;
        this.RebuildCards();
        return Math.Max(max, 80);
    }

    private Task AnimateRootHeightAsync(double fromBox, double toBox, int generation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            From = fromBox,
            To = toBox,
            Duration = SwitchResizeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) => completion.TrySetResult();
        _ = generation;
        this.root.BeginAnimation(FrameworkElement.HeightProperty, animation);
        return completion.Task;
    }

    /// <summary>True when the flyout slides horizontally (taskbar on the left/right).</summary>
    private bool SlidesHorizontally() => this.currentEdge is AbeLeft or AbeRight;

    /// <summary>The off-screen starting offset of the slide, signed for the taskbar edge: bottom slides
    /// up (+Y), top slides down (-Y), left slides right (-X), right slides left (+X).</summary>
    private double SlideStartOffset()
    {
        var distance = Math.Max(1, this.SlidesHorizontally() ? this.root.ActualWidth : this.root.ActualHeight);
        return this.currentEdge switch
        {
            AbeTop => -distance,
            AbeLeft => -distance,
            AbeRight => distance,
            _ => distance, // bottom
        };
    }

    /// <summary>Sets the resting slide offset on the active axis and zeroes the other (clearing animations).</summary>
    private void ApplySlideOffset(double offset)
    {
        this.rootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        this.rootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        this.rootTranslate.X = this.SlidesHorizontally() ? offset : 0;
        this.rootTranslate.Y = this.SlidesHorizontally() ? 0 : offset;
    }

    private static bool IsPartway(double value, double start) =>
        start >= 0 ? value > 0 && value <= start : value < 0 && value >= start;

    /// <summary>
    /// Starts the open slide on the compositor's next frame rather than synchronously, so the
    /// animation clock and the first composited frame stay in lockstep (no first-frame skip). The
    /// root is already at opacity 0 and the start offset here, so it is invisible until the slide
    /// begins. Guarded by the animation generation so a close (or re-open) before the tick fires
    /// cannot animate a stale open.
    /// </summary>
    private void BeginOpenAnimationOnNextFrame()
    {
        var generation = this.animationGeneration;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            if (this.animationGeneration != generation || this.isClosing || !this.IsVisible)
            {
                return;
            }

            this.BeginOpenAnimation();
        };
        CompositionTarget.Rendering += handler;
    }

    private void BeginOpenAnimation()
    {
        var generation = this.animationGeneration;
        var horizontal = this.SlidesHorizontally();
        var start = this.SlideStartOffset();
        var property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var current = horizontal ? this.rootTranslate.X : this.rootTranslate.Y;

        // Start off-screen unless we're already partway there (a re-open mid-close animates from where it is).
        if (!IsPartway(current, start))
        {
            current = start;
            this.ApplySlideOffset(start);
        }

        this.rootTranslate.BeginAnimation(property, new DoubleAnimation
        {
            From = current,
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
        this.log?.Invoke($"flyout open animation generation {generation} edge={this.currentEdge}");
    }

    private void BeginCloseAnimation(int generation)
    {
        var horizontal = this.SlidesHorizontally();
        var property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var current = horizontal ? this.rootTranslate.X : this.rootTranslate.Y;
        var slide = new DoubleAnimation
        {
            From = current,
            To = this.SlideStartOffset(),
            Duration = CloseSlideDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
        slide.Completed += (_, _) => this.CompleteHide(generation);
        this.rootTranslate.BeginAnimation(property, slide);
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
        this.root.BeginAnimation(UIElement.OpacityProperty, null);
        this.stack.BeginAnimation(UIElement.OpacityProperty, null);
        // Clear the switch panel-height animation and hand sizing back to WPF for the next fresh open.
        this.root.BeginAnimation(FrameworkElement.HeightProperty, null);
        this.root.Height = double.NaN;
        this.ApplySlideOffset(0);
        this.root.Opacity = 0;
        this.stack.Opacity = 1;
        this.SizeToContent = SizeToContent.Height;
        this.isClosing = false;
        this.currentFocusProvider = null;
    }

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

        this.footerHost.Content ??= this.CreateFooter();
        this.UpdateRefreshSpinner(states.Any(static state => state.IsRefreshing));
    }

    private double MeasureNaturalHeight()
    {
        // Vary the available width by a sub-pixel amount each call. RebuildCards dirties only the inner
        // stack; measuring root with an unchanged (Width, infinity) constraint early-returns the
        // PREVIOUS provider's cached DesiredSize because the intermediate DockPanel/Border nodes stay
        // valid at that constraint. A tiny width delta changes the constraint at every level, forcing a
        // full re-measure down to the rebuilt cards (0.01 DIP is layout-invisible). Without this,
        // MeasureMaxWindowHeightDip collapsed to the first provider measured and taller switch targets clipped.
        var width = this.Width - ((this.measureNonce++ & 1) * 0.01);
        this.root.Measure(new Size(width, double.PositiveInfinity));
        return Math.Max(this.root.DesiredSize.Height, 80);
    }

    private Task AnimateCardsOpacityAsync(double from, double to, Duration duration, EasingMode easingMode, int generation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = easingMode },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            if (this.IsCurrentAnimation(generation))
            {
                this.stack.Opacity = to;
            }

            completion.TrySetResult();
        };
        this.stack.BeginAnimation(UIElement.OpacityProperty, animation);
        return completion.Task;
    }

    private bool IsCurrentAnimation(int generation) => generation == this.animationGeneration && this.IsVisible && !this.isClosing;

    private void OpenDashboard(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            // Launching the browser deactivates the flyout, which light-dismisses it — the expected feel.
            this.HideFlyout("open dashboard");
        }
        catch (Exception ex)
        {
            this.log?.Invoke($"Failed to open dashboard {url}: {ex.GetType().Name}: {ex.Message}");
        }
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

        // Clicking the header (logo + name) opens the provider's own web dashboard — except when
        // signed out: the subtitle says "open Settings", and sending that very click to the DASHBOARD
        // site had users log in on the provider's website and wonder why the app never reconnected
        // (a website login can't hand the app any tokens; only the in-app sign-in can).
        header.Background = Brushes.Transparent; // make the whole row hit-testable, not just the glyph/text
        if (state.NeedsAuthentication)
        {
            header.Cursor = Cursors.Hand;
            header.ToolTip = $"Open Settings to sign in to {descriptor.Metadata.DisplayName}";
            header.MouseLeftButtonUp += (_, _) =>
            {
                this.HideFlyout("open settings: signed-out header");
                this.openSettings();
            };
        }
        else if (descriptor.Metadata.DashboardUrl is { } dashboard)
        {
            header.Cursor = Cursors.Hand;
            header.ToolTip = $"Open {descriptor.Metadata.DisplayName} dashboard";
            header.MouseLeftButtonUp += (_, _) => OpenDashboard(dashboard);
        }

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

        if (state.Snapshot is { } stamped)
        {
            content.Children.Add(this.CreateUpdatedRow(stamped.UpdatedAt));
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
            // A window whose utilization is unknown is a scalar metric (a count/amount like
            // "3 available" or "$12.30 today"), not a rate limit — render it as a plain label -> value
            // field, never as a bar (a bar would imply a percentage the provider never reported).
            if (extra.UsageKnown)
            {
                content.Children.Add(this.CreateUsageRow(extra.Title, extra.Window, brand));
            }
            else
            {
                content.Children.Add(this.CreateValueRow(extra.Title, extra.Window.ResetDescription ?? "—"));
            }
        }
    }

    /// <summary>A label -> value field row (no bar), used for scalar metrics and credit balances.</summary>
    private UIElement CreateValueRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = this.ResourceBrush("FlyoutForeground"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = this.ResourceBrush("FlyoutForeground"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
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
        if (this.CreatePaceBadge(window) is { } paceBadge)
        {
            value.Children.Add(paceBadge);
        }

        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    // Small colored pace indicator: a triangle (up = at risk, down = under-using) or a level bar
    // (on track), followed by the projected end-of-window usage. Null when disabled or not computable.
    private FrameworkElement? CreatePaceBadge(RateWindow window)
    {
        if (!this.settings.ShowPaceIndicator || PaceCalculator.Compute(window, DateTimeOffset.UtcNow) is not { } pace)
        {
            return null;
        }

        var (color, shape, verb) = PaceVisual(pace.State);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 0, 0) };
        shape.VerticalAlignment = VerticalAlignment.Center;
        shape.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(shape);
        row.Children.Add(new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"{pace.ProjectedPercent:0}%"),
            FontSize = 11,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.ToolTip = string.Create(CultureInfo.InvariantCulture, $"On pace to reach {pace.ProjectedPercent:0}% by reset — {verb}");
        return row;
    }

    // Brand colors + shape per pace band; kept in sync (by RGB) with the widget renderer's PaceColor.
    private static (Color Color, Shape Shape, string Verb) PaceVisual(PaceState state) => state switch
    {
        PaceState.AtRisk => (PaceRed, PaceTriangle(up: true, PaceRed), "on track to run out early"),
        PaceState.Underusing => (PaceBlue, PaceTriangle(up: false, PaceBlue), "barely using your quota"),
        _ => (PaceGreen, PaceLevelBar(PaceGreen), "on track"),
    };

    private static readonly Color PaceRed = Color.FromRgb(0xE5, 0x48, 0x4D);
    private static readonly Color PaceGreen = Color.FromRgb(0x30, 0xA4, 0x6C);
    private static readonly Color PaceBlue = Color.FromRgb(0x4C, 0x8D, 0xF0);

    private static Polygon PaceTriangle(bool up, Color color) => new()
    {
        Width = 9,
        Height = 7,
        Fill = new SolidColorBrush(color),
        Points = up
            ? [new Point(0, 7), new Point(4.5, 0), new Point(9, 7)]
            : [new Point(0, 0), new Point(9, 0), new Point(4.5, 7)],
    };

    private static Rectangle PaceLevelBar(Color color) => new()
    {
        Width = 9,
        Height = 3,
        RadiusX = 1,
        RadiusY = 1,
        Fill = new SolidColorBrush(color),
    };

    private UIElement CreateCreditsRow(CreditsSnapshot credits)
    {
        var value = credits.Limit is { } limit
            ? string.Create(CultureInfo.InvariantCulture, $"{credits.Remaining:0.##} of {limit:0.##} {credits.Unit}")
            : string.Create(CultureInfo.InvariantCulture, $"{credits.Remaining:0.##} {credits.Unit}");
        return this.CreateValueRow("Credits", value);
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

    // First-run / empty state: shown when no providers are enabled. Welcomes the user and points them
    // straight into Settings to connect their first provider, rather than leaving a blank popup.
    private UIElement CreateEmptyCard()
    {
        var card = this.CreateCardShell();
        var content = new StackPanel { Orientation = Orientation.Vertical };

        content.Children.Add(new TextBlock
        {
            Text = "Welcome to CodexWinBar",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.ResourceBrush("FlyoutForeground"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        content.Children.Add(new TextBlock
        {
            Text = "Connect the AI providers you use to see your usage limits at a glance. Codex, Claude and Copilot "
                + "sign in from Settings; the rest just need an API key. Nothing is connected until you sign in.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.ResourceBrush("FlyoutSecondaryForeground"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var button = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "Open Settings",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
            },
        };
        button.MouseLeftButtonUp += (_, _) =>
        {
            this.HideFlyout("empty: open settings");
            this.openSettings();
        };
        content.Children.Add(button);

        card.Child = content;
        return card;
    }

    private Border CreateCardShell()
    {
        var card = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Background = this.ResourceBrush("FlyoutCardBackground"),
            CornerRadius = new CornerRadius(8),
            SnapsToDevicePixels = true,
        };
        // The card background is deliberately opaque, so ClearType is safe even though the outer
        // shadow margin belongs to an AllowsTransparency window.
        RenderOptions.SetClearTypeHint(card, ClearTypeHint.Enabled);
        return card;
    }

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
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // refresh
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // spacer
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // update (only when staged)
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // settings
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // close
        var refresh = this.CreateIconButton(this.CreateRefreshGlyph(), "Refresh");
        refresh.Click += async (_, _) => await this.RefreshAllAsync();
        footer.Children.Add(refresh);

        // Update button (left of the gear): hidden until an update is found, then it walks through
        // download-arrow -> circular progress -> green restart, or a red X on failure. Click behaviour
        // depends on the current stage (RenderUpdateButton keeps its look in sync).
        this.updateButton = this.CreateIconButton(null!, string.Empty);
        this.updateButton.Click += (_, _) => this.OnUpdateButtonClicked();
        Grid.SetColumn(this.updateButton, 2);
        footer.Children.Add(this.updateButton);
        this.RenderUpdateButton();

        var settingsButton = this.CreateIconButton(LogoImages.IconGlyph(LogoImages.SettingsGlyph, 13), "Settings");
        settingsButton.Click += (_, _) => { this.HideFlyout("close: settings"); this.openSettings(); };
        Grid.SetColumn(settingsButton, 3);
        footer.Children.Add(settingsButton);
        // The X only closes the flyout, never the app — quitting is reserved for the tray menu.
        var closeButton = this.CreateIconButton(LogoImages.IconGlyph(LogoImages.CloseGlyph, 10), "Close");
        closeButton.Click += (_, _) => this.HideFlyout("close: x-button");
        Grid.SetColumn(closeButton, 4);
        footer.Children.Add(closeButton);
        return footer;
    }

    /// <summary>An update was found; show the download-arrow button. Safe to call off the UI thread.</summary>
    public void SetUpdateAvailable() => this.SetUpdateStage(UpdateStage.Available, progress: 0, error: null);

    /// <summary>Download progress (0-100) while an update is downloading.</summary>
    public void SetUpdateProgress(double percent) => this.SetUpdateStage(UpdateStage.Downloading, percent, null);

    /// <summary>The update finished downloading and is staged; show the green restart button.</summary>
    public void SetUpdateReady() => this.SetUpdateStage(UpdateStage.Ready, progress: 100, error: null);

    /// <summary>The download/apply failed; show the red X with <paramref name="message"/> on hover.</summary>
    public void SetUpdateError(string message) => this.SetUpdateStage(UpdateStage.Error, this.updateProgress, message);

    private void SetUpdateStage(UpdateStage stage, double progress, string? error)
    {
        _ = this.Dispatcher.BeginInvoke(() =>
        {
            this.updateStage = stage;
            this.updateProgress = progress;
            this.updateError = error;
            this.RenderUpdateButton();
        });
    }

    private void OnUpdateButtonClicked()
    {
        switch (this.updateStage)
        {
            case UpdateStage.Available:
            case UpdateStage.Error:      // retry the download
                this.SetUpdateStage(UpdateStage.Downloading, progress: 0, error: null);
                this.onStartUpdateDownload();
                break;
            case UpdateStage.Ready:
                this.HideFlyout("close: apply-update");
                this.onApplyUpdate();
                break;
            default:
                break;               // ignore clicks while downloading
        }
    }

    private static readonly Color UpdateBlue = Color.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly Color UpdateGreen = Color.FromRgb(0x16, 0xA3, 0x4A);
    private static readonly Color UpdateRed = Color.FromRgb(0xE0, 0x4B, 0x4B);

    /// <summary>Repaints the footer update button to match <see cref="updateStage"/>.</summary>
    private void RenderUpdateButton()
    {
        if (this.updateButton is null)
        {
            return;
        }

        var button = this.updateButton;
        button.Visibility = this.updateStage == UpdateStage.None ? Visibility.Collapsed : Visibility.Visible;
        switch (this.updateStage)
        {
            case UpdateStage.Available:
                button.Content = AccentGlyph(LogoImages.DownloadGlyph, 14, UpdateBlue);
                button.ToolTip = "Update available — click to download";
                button.IsHitTestVisible = true;
                break;
            case UpdateStage.Downloading:
                button.Content = BuildProgressRing(this.updateProgress);
                button.ToolTip = $"Downloading update… {this.updateProgress:0}%";
                button.IsHitTestVisible = false;
                break;
            case UpdateStage.Ready:
                button.Content = AccentGlyph(LogoImages.RefreshGlyph, 14, UpdateGreen);
                button.ToolTip = "Update ready — click to restart";
                button.IsHitTestVisible = true;
                break;
            case UpdateStage.Error:
                button.Content = AccentGlyph(LogoImages.CloseGlyph, 12, UpdateRed);
                button.ToolTip = this.updateError ?? "Update failed — click to retry";
                button.IsHitTestVisible = true;
                break;
            default:
                break;
        }
    }

    private static TextBlock AccentGlyph(string glyph, double size, Color color)
    {
        var text = LogoImages.IconGlyph(glyph, size);
        text.Foreground = new SolidColorBrush(color);
        return text;
    }

    /// <summary>A small determinate circular progress ring (0-100), drawn as a faint full circle with a
    /// blue arc sweeping clockwise from the top.</summary>
    private static UIElement BuildProgressRing(double percent)
    {
        const double size = 18;
        const double thickness = 2.5;
        var radius = (size - thickness) / 2;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(Color.FromArgb(0x33, UpdateBlue.R, UpdateBlue.G, UpdateBlue.B)),
            StrokeThickness = thickness,
            Fill = Brushes.Transparent,
        });

        var angle = Math.Clamp(percent, 0, 100) / 100.0 * 360.0;
        if (angle > 0.5)
        {
            var center = new Point(size / 2, size / 2);
            var radians = angle * Math.PI / 180.0;
            var end = new Point(center.X + (radius * Math.Sin(radians)), center.Y - (radius * Math.Cos(radians)));
            var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = angle > 180,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            grid.Children.Add(new Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(UpdateBlue),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        return grid;
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

    /// <summary>
    /// Subtle "Updated Xm ago" freshness caption at the bottom of a provider card, recomputed on every
    /// rebuild (state changes, open, and the minute countdown tick — no extra timer needed). Data older
    /// than <see cref="StaleDataAge"/> dims further to signal staleness; the caption also shows next to
    /// the error subtitle so the user knows how old the last-good data is.
    /// </summary>
    private UIElement CreateUpdatedRow(DateTimeOffset updatedAt) => new TextBlock
    {
        Text = UpdatedText(updatedAt),
        FontSize = 10,
        Foreground = this.ResourceBrush("FlyoutSecondaryForeground"),
        Opacity = DateTimeOffset.UtcNow - updatedAt > StaleDataAge ? 0.6 : 1.0,
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>Relative freshness text for a snapshot, e.g. "Updated just now" / "Updated 5m ago".</summary>
    private static string UpdatedText(DateTimeOffset updatedAt)
    {
        var age = DateTimeOffset.UtcNow - updatedAt;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Updated just now";
        }

        if (age.TotalHours < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Updated {(int)age.TotalMinutes}m ago");
        }

        if (age.TotalDays < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Updated {(int)age.TotalHours}h ago");
        }

        return string.Create(CultureInfo.InvariantCulture, $"Updated {(int)age.TotalDays}d ago");
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

    /// <summary>Height of the work area (screen minus taskbar) for the anchor's monitor, in DIP.</summary>
    private double WorkAreaHeightDip(DrawingRectangle anchorPhysicalPx)
    {
        var monitor = MonitorFromRect(ref anchorPhysicalPx, MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        _ = GetMonitorInfo(monitor, ref info);
        var transform = (this.source ?? (HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var heightPx = info.Work.Bottom - info.Work.Top;
        return heightPx * (transform.M22 <= 0 ? 1 : transform.M22);
    }

    /// <summary>
    /// Panel top-left (physical px) placed just off the anchor toward the screen centre, per taskbar
    /// edge (bottom→above, top→below, left→right of, right→left of), clamped to the work area.
    /// NOTE: only the bottom edge is live-tested; the top/left/right branches (and the matching
    /// slide-direction logic) are verified by reasoning only, because Windows 11 can't move the taskbar
    /// off the bottom. Re-verify on a real top/side taskbar (Win10 or a Win11 taskbar tool).
    /// </summary>
    private static (double X, double Y) PanelTopLeftPx(DrawingRectangle anchor, double panelW, double panelH, NativeRect work, int edge)
    {
        double x;
        double y;
        switch (edge)
        {
            case AbeTop:
                x = anchor.Right - panelW;
                y = anchor.Bottom + GapPhysicalPx;
                break;
            case AbeLeft:
                x = anchor.Right + GapPhysicalPx;
                y = anchor.Top;
                break;
            case AbeRight:
                x = anchor.Left - GapPhysicalPx - panelW;
                y = anchor.Top;
                break;
            default: // AbeBottom
                x = anchor.Right - panelW;
                y = anchor.Top - GapPhysicalPx - panelH;
                break;
        }

        x = Math.Max(work.Left, Math.Min(x, work.Right - panelW));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - panelH));
        return (x, y);
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
        var (panelX, panelY) = PanelTopLeftPx(anchorPhysicalPx, panelWidthPx, panelHeightPx, work, this.currentEdge);
        var x = panelX - shadowMarginXPx;
        var y = panelY - shadowMarginYPx;
        x = Math.Max(work.Left, Math.Min(x, work.Right - widthPx));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - heightPx));
        _ = SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        return transform.Transform(new Point(x, y));
    }

    /// <summary>
    /// Resizes the shown HWND to <paramref name="heightDip"/> tall (top-left preserved). Rescope needs
    /// this because WPF applies a <c>this.Height</c> change too late for the following PlacePhysical
    /// size read; PlacePhysical then repositions with the corrected size.
    /// </summary>
    private void ResizeHwndHeight(double heightDip)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr))
        {
            return;
        }

        var transform = (this.source ?? (HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var heightPx = Math.Max(1, (int)Math.Round(heightDip / Math.Max(transform.M22, 0.01)));
        _ = SetWindowPos(hwnd, HwndTop, wr.Left, wr.Top, wr.Right - wr.Left, heightPx, SwpNoZorder | SwpNoActivate);
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

        var previousWidthPx = wr.Right - wr.Left;
        var heightPx = wr.Bottom - wr.Top;
        var monitor = MonitorFromRect(ref anchorPhysicalPx, MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        _ = GetMonitorInfo(monitor, ref info);
        var work = info.Work;
        var transform = (this.source ?? (HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        // Native SetWindowPos calls can feed the physical width back into Window.Width. Reusing that
        // mutable value on another DPI then stretches or compresses the flyout by the monitor ratio.
        // Always scale from the immutable design width instead.
        var widthPx = IntendedWindowWidthPx(transform.M11);
        var shadowMarginXPx = (int)Math.Round(ShadowMarginDip / Math.Max(transform.M11, 0.01));
        var shadowMarginYPx = (int)Math.Round(ShadowMarginDip / Math.Max(transform.M22, 0.01));
        var panelWidthPx = Math.Max(1, widthPx - (shadowMarginXPx * 2));
        var panelHeightPx = Math.Max(1, heightPx - (shadowMarginYPx * 2));
        var (panelXd, panelYd) = PanelTopLeftPx(anchorPhysicalPx, panelWidthPx, panelHeightPx, work, this.currentEdge);
        var panelX = (int)panelXd;
        var panelY = (int)panelYd;
        var x = (int)Math.Max(work.Left, Math.Min(panelX - shadowMarginXPx, work.Right - widthPx));
        var y = (int)Math.Max(work.Top, Math.Min(panelY - shadowMarginYPx, work.Bottom - heightPx));
        _ = SetWindowPos(hwnd, HwndTop, x, y, widthPx, heightPx, SwpNoActivate);
        if (previousWidthPx != widthPx)
        {
            this.log?.Invoke($"flyout width corrected from {previousWidthPx}px to {widthPx}px for current DPI");
        }

        this.log?.Invoke($"flyout placed edge={this.currentEdge} at ({x},{y}) size {widthPx}x{heightPx}; panel at ({x + shadowMarginXPx},{y + shadowMarginYPx}) size {panelWidthPx}x{panelHeightPx} (physical)");
    }

    internal static int IntendedWindowWidthPx(double deviceToDipX) =>
        Math.Max(1, (int)Math.Round((FlyoutWidthDip + (ShadowMarginDip * 2)) / Math.Max(deviceToDipX, 0.01)));

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDisplayChange || (msg is WmDpiChanged && this.IsOpen && !this.suppressDpiClose))
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
        if (code >= 0 && IsMouseDownMessage(wParam.ToInt32()))
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            // No open-grace here: a real mouse-down outside the panel is always an intentional dismiss.
            // (The opening click can't reach this hook — it's installed only after the flyout opens — so
            // there's nothing to protect against.) The grace stays on the deactivation path only, where a
            // spurious focus-loss right after opening must be ignored. Applying it here ate the first
            // click when the user opened and clicked away quickly (and switching re-armed the timer).
            if (this.IsOpen)
            {
                // The HWND is fixed to the tallest provider, so a shorter provider leaves transparent
                // space above the panel that is still inside the window rect. Dismiss against the VISIBLE
                // panel region (bottom-anchored) so clicking that empty gap closes the flyout as expected.
                var flyoutRect = this.VisiblePanelPhysicalRect();
                var contains = flyoutRect.Contains(hook.Point.X, hook.Point.Y);
                var widget = IsWidgetClick(hook.Point);
                // A click on the CodexWinBar widget is a switch/toggle, not an outside-dismiss — let the
                // widget's own click routing handle it. Dismissing here would race the switch and turn it
                // into a close+reopen slide instead of the in-place cross-fade.
                if (!contains && !widget)
                {
                    _ = this.Dispatcher.BeginInvoke(() => this.HideFlyout("close: outside-click"));
                }
            }
        }

        return CallNextHookEx(this.hookHandle, code, wParam, lParam);
    }

    private static bool IsWidgetClick(NativePoint point)
    {
        var hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        // The widget registers a per-instance class ("CodexWinBarWidget_<n>") so a second widget on
        // another taskbar can't bind the first's WndProc, so match the prefix rather than the exact
        // name — an exact "CodexWinBarWidget" match silently stopped recognizing widget clicks once
        // multi-monitor made the class names unique, turning a same-provider toggle into a
        // dismiss-then-reopen bob. The trailing underscore excludes the controller window.
        return length > 0 && new string(buffer, 0, length).StartsWith("CodexWinBarWidget_", StringComparison.Ordinal);
    }

    /// <summary>
    /// The on-screen rect of the visible flyout (panel plus its shadow margin), bottom-anchored inside
    /// the fixed-height window. Everything above it in the window is invisible transparent space.
    /// </summary>
    private DrawingRectangle VisiblePanelPhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr))
        {
            return DrawingRectangle.Empty;
        }

        var transform = (this.source ?? (HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dipToPxY = 1.0 / Math.Max(transform.M22, 0.01);
        var panelBoxDip = this.root.ActualHeight > 0 ? this.root.ActualHeight : (this.sessionWindowHeightDip - (2 * ShadowMarginDip));
        // Visible height = panel box + top and bottom shadow margins (the soft shadow reads as part
        // of the flyout, so clicks on it should not dismiss).
        var visibleHeightPx = (int)Math.Round((panelBoxDip + (2 * ShadowMarginDip)) * dipToPxY);
        var top = wr.Bottom - Math.Min(visibleHeightPx, wr.Bottom - wr.Top);
        return DrawingRectangle.FromLTRB(wr.Left, top, wr.Right, wr.Bottom);
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
            ["FlyoutPanelBackground"] = new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x2A, 0x2A) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
            ["FlyoutSubtleBorder"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x30 : (byte)0x24, foreground.R, foreground.G, foreground.B)),
            // Opaque equivalents of the previous translucent colors over FlyoutPanelBackground.
            ["FlyoutCardBackground"] = new SolidColorBrush(dark ? Color.FromRgb(0x30, 0x30, 0x30) : Color.FromRgb(0xFB, 0xFB, 0xFB)),
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

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint CbSize;
        public IntPtr Hwnd;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rect;
        public IntPtr LParam;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    /// <summary>The screen edge the primary taskbar is docked to (ABE_*), or bottom when unknown.</summary>
    private static int QueryTaskbarEdge()
    {
        var data = new AppBarData { CbSize = (uint)Marshal.SizeOf<AppBarData>() };
        var result = SHAppBarMessage(AbmGetTaskbarPos, ref data);
        return result == IntPtr.Zero ? AbeBottom : (int)data.Edge;
    }
}
