using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CodexWinBar.App.Assets;
using CodexWinBar.App.Interop;
using CodexWinBar.App.Startup;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using CodexWinBar.Providers;
using CodexWinBar.Providers.Copilot;

namespace CodexWinBar.App.Settings;

/// <summary>
/// WPF settings window for the Windows-native CodexWinBar app.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const double NavWidth = 200;
    private static SettingsWindow? current;

    private readonly ConfigStore configStore;
    private readonly UiSettingsStore uiStore;
    private readonly IUsageStore usageStore;
    private readonly Action applySettings;
    private readonly IReadOnlyList<ProviderDescriptor> providers;
    private readonly Grid contentHost = new();
    private readonly List<NavRow> navRows = [];
    private readonly Dictionary<ProviderId, ProviderCard> providerCards = [];
    private readonly HttpClient http = new();
    private readonly bool isDark;
    private readonly SolidColorBrush accentBrush;
    private CancellationTokenSource? copilotSignIn;
    private TextBlock? activeCopilotStatus;
    private StackPanel? activeCopilotDetail;
    private string activePane = "General";

    private SettingsWindow(ConfigStore cfg, UiSettingsStore ui, IUsageStore store, Action applySettings)
    {
        this.configStore = cfg;
        this.uiStore = ui;
        this.usageStore = store;
        this.applySettings = applySettings;
        this.providers = ProviderCatalog.CreateAll();
        this.isDark = !SystemAppsUseLightTheme();
        this.accentBrush = new SolidColorBrush(ReadAccentColor());
        this.accentBrush.Freeze();

        this.Title = "CodexWinBar Settings";
        this.Width = 980;
        this.Height = 700;
        this.MinWidth = 760;
        this.MinHeight = 500;
        this.WindowStyle = WindowStyle.SingleBorderWindow;
        this.ShowInTaskbar = true;
        this.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        this.Resources.MergedDictionaries.Add(SettingsTheme.Create(this.isDark, this.accentBrush.Color));
        this.Background = Brushes.Transparent;
        this.Foreground = Brush("SettingsForeground");
        this.Content = this.BuildRoot();

        this.SourceInitialized += this.OnSourceInitialized;
        this.usageStore.StateChanged += this.OnUsageStateChanged;
        this.Closed += (_, _) =>
        {
            this.usageStore.StateChanged -= this.OnUsageStateChanged;
            this.copilotSignIn?.Cancel();
            this.copilotSignIn?.Dispose();
            this.http.Dispose();
            if (ReferenceEquals(current, this))
            {
                current = null;
            }
        };

        this.Navigate("General");
    }

    /// <summary>
    /// Shows the singleton settings window, or activates the existing instance.
    /// </summary>
    public static void ShowOrActivate(ConfigStore cfg, UiSettingsStore ui, IUsageStore store, Action applySettings)
    {
        if (current is { IsVisible: true })
        {
            if (current.WindowState == WindowState.Minimized)
            {
                current.WindowState = WindowState.Normal;
            }

            current.Activate();
            return;
        }

        current = new SettingsWindow(cfg, ui, store, applySettings);
        current.Show();
        current.Activate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Full Mica recipe (frame extension + backdrop + transparent surface) lives in WpfDwm.
        WpfDwm.ApplyWindowChrome(this, this.isDark);
    }

    private Grid BuildRoot()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new StackPanel
        {
            Margin = new Thickness(8, 20, 8, 0),
            Background = Brushes.Transparent,
        };

        foreach (var item in new[]
        {
            ("General", "\uE713"),
            ("Display", "\uE7F4"),
            ("Providers", "\uE8D4"),
            ("About", "\uE946"),
        })
        {
            var row = new NavRow(item.Item1, item.Item2, () => this.Navigate(item.Item1))
            {
                Margin = new Thickness(4, 0, 4, 2),
            };
            this.navRows.Add(row);
            nav.Children.Add(row);
        }

        Grid.SetColumn(nav, 0);
        root.Children.Add(nav);

        var contentFrame = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var scroller = new ScrollViewer
        {
            Padding = new Thickness(24, 20, 24, 20),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = contentFrame,
        };
        contentFrame.Children.Add(this.contentHost);
        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);
        return root;
    }

    private void Navigate(string pane)
    {
        this.activePane = pane;
        foreach (var row in this.navRows)
        {
            row.IsSelected = string.Equals(row.Label, pane, StringComparison.Ordinal);
        }

        switch (pane)
        {
            case "General":
                this.ShowGeneral();
                break;
            case "Display":
                this.ShowDisplay();
                break;
            case "Providers":
                this.ShowProviders();
                break;
            case "About":
                this.ShowAbout();
                break;
        }
    }

    private void SetContent(UIElement element)
    {
        this.contentHost.Children.Clear();
        this.contentHost.Children.Add(element);
    }

    private void ShowGeneral()
    {
        var settings = this.uiStore.Load();
        var panel = Page("General");
        var group = CardGroup();

        group.Children.Add(this.SettingCard("\uE77B", "Launch at login", "Start CodexWinBar when you sign in",
            new ToggleSwitch(StartupManager.IsEnabled(), isChecked =>
            {
                StartupManager.SetEnabled(isChecked);
                this.SaveUi(ui => ui.LaunchAtLogin = isChecked);
            })));

        var cadence = Combo(
            new[]
            {
                new ComboOption<int?>("Manual", null),
                new ComboOption<int?>("1 min", 1),
                new ComboOption<int?>("2 min", 2),
                new ComboOption<int?>("5 min", 5),
                new ComboOption<int?>("15 min", 15),
                new ComboOption<int?>("30 min", 30),
            },
            settings.RefreshCadenceMinutes);
        cadence.SelectionChanged += (_, _) =>
        {
            if (cadence.SelectedItem is ComboOption<int?> option)
            {
                this.SaveUi(ui => ui.RefreshCadenceMinutes = option.Value);
            }
        };
        group.Children.Add(this.SettingCard("\uE823", "Refresh cadence", "How often provider usage is fetched", cadence));

        group.Children.Add(this.SettingCard("\uE9D9", "Provider status checks", "Poll provider status pages for incidents",
            new ToggleSwitch(settings.StatusChecksEnabled, isChecked => this.SaveUi(ui => ui.StatusChecksEnabled = isChecked))));

        group.Children.Add(this.CreateQuotaNotificationsCard(settings));

        panel.Children.Add(group);
        this.SetContent(panel);
    }

    private void ShowDisplay()
    {
        var settings = this.uiStore.Load();
        var panel = Page("Display");
        var group = CardGroup();

        var mode = Combo(Enum.GetValues<WidgetMode>().Select(value => new ComboOption<WidgetMode>(value.ToString(), value)), settings.WidgetMode);
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedItem is ComboOption<WidgetMode> option)
            {
                this.SaveUi(ui => ui.WidgetMode = option.Value);
            }
        };
        group.Children.Add(this.SettingCard("\uE8A7", "Widget mode", null, mode));

        var side = Combo(
            new[]
            {
                new ComboOption<WidgetSide>("Right", WidgetSide.Right),
                new ComboOption<WidgetSide>("Left", WidgetSide.Left),
            },
            settings.WidgetSide);
        side.SelectionChanged += (_, _) =>
        {
            if (side.SelectedItem is ComboOption<WidgetSide> option)
            {
                this.SaveUi(ui =>
                {
                    ui.WidgetSide = option.Value;
                    ui.WidgetMode = option.Value == WidgetSide.Left ? WidgetMode.Overlay : ui.WidgetMode;
                });
            }
        };
        group.Children.Add(this.SettingCard("\uE8AB", "Widget side", "Left pins the widget to the taskbar's left edge", side));

        var textMode = Combo(Enum.GetValues<DisplayTextMode>().Select(value => new ComboOption<DisplayTextMode>(value.ToString(), value)), settings.DisplayTextMode);
        textMode.SelectionChanged += (_, _) =>
        {
            if (textMode.SelectedItem is ComboOption<DisplayTextMode> option)
            {
                this.SaveUi(ui => ui.DisplayTextMode = option.Value);
            }
        };
        group.Children.Add(this.SettingCard("\uE8D2", "Display text", null, textMode));

        group.Children.Add(this.SettingCard("\uE9F5", "Show used instead of remaining", null,
            new ToggleSwitch(settings.UsageBarsShowUsed, isChecked => this.SaveUi(ui => ui.UsageBarsShowUsed = isChecked))));

        group.Children.Add(this.SettingCard("\uE916", "Absolute reset times", null,
            new ToggleSwitch(settings.ResetTimesShowAbsolute, isChecked => this.SaveUi(ui => ui.ResetTimesShowAbsolute = isChecked))));

        panel.Children.Add(group);
        this.SetContent(panel);
    }

    private void ShowProviders()
    {
        var panel = Page("Providers");
        var group = CardGroup();
        this.providerCards.Clear();
        this.activeCopilotStatus = null;
        this.activeCopilotDetail = null;

        foreach (var descriptor in this.providers)
        {
            var card = this.CreateProviderCard(descriptor);
            this.providerCards[descriptor.Id] = card;
            group.Children.Add(card);
        }

        panel.Children.Add(group);
        this.SetContent(panel);
    }

    private ProviderCard CreateProviderCard(ProviderDescriptor descriptor)
    {
        var entry = this.configStore.EntryFor(this.configStore.Load(), descriptor.Id);
        var enabled = entry.Enabled ?? descriptor.Metadata.DefaultEnabled;
        var status = new TextBlock();
        var detail = new StackPanel { Margin = new Thickness(40, 14, 0, 0) };
        var toggle = new ToggleSwitch(enabled, isChecked => this.SaveProviderEntry(descriptor.Id, item => item with { Enabled = isChecked }));
        var card = new ProviderCard(descriptor, this.ProviderLogo(descriptor, 24), status, toggle, detail, this.ProviderNeedsConfig(descriptor.Id));
        card.SetResourceReference(Control.ForegroundProperty, "SettingsForeground");
        this.FillProviderCard(descriptor, status, detail);
        return card;
    }

    private void FillProviderCard(ProviderDescriptor descriptor, TextBlock status, StackPanel detail)
    {
        status.Text = this.ProviderSummary(descriptor.Id);
        status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
        detail.Children.Clear();

        if (descriptor.Id is ProviderId.OpenRouter or ProviderId.Zai or ProviderId.OpenAIAdmin)
        {
            this.AddApiKeyEditor(descriptor, detail);
        }
        else if (descriptor.Id == ProviderId.Copilot)
        {
            this.AddCopilotEditor(detail, status);
        }
        else
        {
            detail.Children.Add(Text(this.CredentialHint(descriptor.Id)));
            detail.Children.Add(ButtonWithIcon(LogoImages.RefreshGlyph, "Refresh", async () => await this.RefreshProviderAsync(descriptor.Id)));
        }
    }

    private bool ProviderNeedsConfig(ProviderId id) =>
        id is ProviderId.OpenRouter or ProviderId.Zai or ProviderId.OpenAIAdmin or ProviderId.Copilot or ProviderId.Codex or ProviderId.Claude or ProviderId.Gemini;

    private FrameworkElement ProviderLogo(ProviderDescriptor descriptor, double size)
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
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(Color.FromRgb(descriptor.Branding.R, descriptor.Branding.G, descriptor.Branding.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = InitialFor(descriptor),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static string InitialFor(ProviderDescriptor descriptor)
    {
        var name = descriptor.Metadata.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString(CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
    }

    private void AddApiKeyEditor(ProviderDescriptor descriptor, Panel detail)
    {
        var entry = this.configStore.EntryFor(this.configStore.Load(), descriptor.Id);
        detail.Children.Add(Text(string.IsNullOrWhiteSpace(entry.ApiKey) ? "No API key saved." : "API key saved: ..."));
        var password = StyledPasswordBox();
        detail.Children.Add(password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("Save", () =>
        {
            this.SaveProviderEntry(descriptor.Id, item => item with { ApiKey = password.Password, Source = "api" });
            password.Clear();
            this.RefreshProviderCard(descriptor.Id);
        }, true));
        buttons.Children.Add(Button("Clear", () =>
        {
            this.SaveProviderEntry(descriptor.Id, item => item with { ApiKey = null });
            this.RefreshProviderCard(descriptor.Id);
        }));
        buttons.Children.Add(ButtonWithIcon(LogoImages.RefreshGlyph, "Refresh", async () => await this.RefreshProviderAsync(descriptor.Id)));
        detail.Children.Add(buttons);
    }

    private void AddCopilotEditor(StackPanel detail, TextBlock status)
    {
        this.activeCopilotStatus = status;
        this.activeCopilotDetail = detail;
        detail.Children.Add(Text("Sign in with GitHub device flow. The token is stored in the Copilot apiKey field."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ButtonWithIcon(LogoImages.SignInGlyph, "Sign in", async () => await this.StartCopilotSignInAsync(), true));
        buttons.Children.Add(ButtonWithIcon(LogoImages.RefreshGlyph, "Refresh", async () => await this.RefreshProviderAsync(ProviderId.Copilot)));
        detail.Children.Add(buttons);
    }

    private async Task StartCopilotSignInAsync()
    {
        this.copilotSignIn?.Cancel();
        this.copilotSignIn?.Dispose();
        this.copilotSignIn = new CancellationTokenSource();
        var ct = this.copilotSignIn.Token;
        var status = this.activeCopilotStatus;
        var detail = this.activeCopilotDetail;

        try
        {
            if (status is not null)
            {
                status.Text = "Starting GitHub device flow...";
            }

            var info = await CopilotAuth.StartAsync(this.http, ct);
            if (detail is not null)
            {
                detail.Children.Add(SectionHeader(info.UserCode));
                detail.Children.Add(ButtonWithIcon(LogoImages.CopyGlyph, "Copy code", () => Clipboard.SetText(info.UserCode)));
                detail.Children.Add(ButtonWithIcon(LogoImages.ExternalLinkGlyph, "Open GitHub", () => OpenUri(info.VerificationUri)));
                detail.Children.Add(Button("Cancel", () => this.copilotSignIn?.Cancel()));
            }

            OpenUri(info.VerificationUri);
            if (status is not null)
            {
                status.Text = $"Waiting for GitHub authorization. Expires at {info.ExpiresAt.LocalDateTime:t}.";
            }

            var token = await CopilotAuth.PollAsync(this.http, info, ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                if (status is not null)
                {
                    status.Text = "GitHub authorization expired or was denied.";
                }

                return;
            }

            this.SaveProviderEntry(ProviderId.Copilot, entry => entry with { ApiKey = token, Source = "oauth" });
            if (status is not null)
            {
                status.Text = "GitHub authorization complete.";
            }

            await this.RefreshProviderAsync(ProviderId.Copilot);
        }
        catch (OperationCanceledException)
        {
            if (status is not null)
            {
                status.Text = "GitHub authorization cancelled.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            if (status is not null)
            {
                status.Text = ex.Message;
            }
        }
    }

    private string ProviderSummary(ProviderId id)
    {
        var state = this.usageStore.States.FirstOrDefault(item => item.Provider == id);
        if (state is null)
        {
            return "Not configured";
        }

        if (state.NeedsAuthentication)
        {
            return "Needs authentication";
        }

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            return state.Error;
        }

        var identity = state.Snapshot?.Identity;
        var name = identity?.AccountEmail ?? identity?.AccountOrganization ?? identity?.Plan;
        return string.IsNullOrWhiteSpace(name) ? "Signed in" : name;
    }

    private string CredentialHint(ProviderId id) => id switch
    {
        ProviderId.Codex => @"Codex reads credentials from %CODEX_HOME%\auth.json or %USERPROFILE%\.codex\auth.json.",
        ProviderId.Claude => @"Claude reads credentials from %USERPROFILE%\.claude\.credentials.json.",
        ProviderId.Gemini => @"Gemini reads credentials from %USERPROFILE%\.gemini\oauth_creds.json.",
        _ => "This provider uses credentials managed outside this settings pane.",
    };

    private async Task RefreshProviderAsync(ProviderId id)
    {
        try
        {
            await this.usageStore.RefreshProviderAsync(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            if (this.providerCards.TryGetValue(id, out var card))
            {
                card.Status.Text = ex.Message;
            }
        }
    }

    private void ShowAbout()
    {
        var panel = Page("About");
        var group = CardGroup();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        group.Children.Add(this.HeroCard(version));
        group.Children.Add(LinkCard("\uE8A7", "GitHub repo", "https://github.com/steipete/CodexWinBar"));
        group.Children.Add(LinkCard("\uE8A7", "Upstream CodexBar", "https://github.com/steipete/CodexBar"));
        group.Children.Add(LinkCard("\uE8A7", "MIT license", "https://github.com/steipete/CodexWinBar/blob/main/LICENSE"));
        panel.Children.Add(group);
        this.SetContent(panel);
    }

    private void SaveUi(Action<UiSettings> update)
    {
        var settings = this.uiStore.Load();
        update(settings);
        this.uiStore.Save(settings);
        this.applySettings();
    }

    private void SaveProviderEntry(ProviderId id, Func<ProviderConfigEntry, ProviderConfigEntry> update)
    {
        var config = this.configStore.Load();
        var entry = this.configStore.EntryFor(config, id);
        this.configStore.Save(this.configStore.WithEntry(config, update(entry)));
        this.applySettings();
    }

    private QuotaSettingsCard CreateQuotaNotificationsCard(UiSettings settings)
    {
        var editor = new QuotaWarningsEditor(this);
        var detail = editor.Build(settings);

        QuotaSettingsCard? card = null;
        var toggle = new ToggleSwitch(settings.QuotaNotificationsEnabled, isChecked =>
        {
            this.SaveUi(ui => ui.QuotaNotificationsEnabled = isChecked);
            if (isChecked && card is not null)
            {
                card.IsExpanded = true;
            }
        });
        card = new QuotaSettingsCard(LogoImages.IconGlyph("\uE7F4", 20), toggle, detail);
        return card;
    }

    private void SaveQuotaProviderOverride(
        ProviderId providerId,
        bool isSession,
        bool enabled,
        IReadOnlyList<int> thresholds)
    {
        var settings = this.uiStore.Load();
        this.SaveProviderEntry(providerId, entry =>
        {
            var existing = entry.QuotaWarnings;
            var session = existing?.Session ?? CreateQuotaWarningWindow(settings.QuotaSessionEnabled, settings.QuotaSessionThresholds);
            var weekly = existing?.Weekly ?? CreateQuotaWarningWindow(settings.QuotaWeeklyEnabled, settings.QuotaWeeklyThresholds);
            var updated = CreateQuotaWarningWindow(enabled, thresholds);
            return entry with
            {
                QuotaWarnings = new QuotaWarnings
                {
                    Session = isSession ? updated : session,
                    Weekly = isSession ? weekly : updated,
                },
            };
        });
    }

    private void ResetQuotaProviderOverride(ProviderId providerId)
    {
        this.SaveProviderEntry(providerId, entry => entry with { QuotaWarnings = null });
    }

    private static QuotaWarningWindow CreateQuotaWarningWindow(bool enabled, IReadOnlyList<int> thresholds) => new()
    {
        Enabled = enabled,
        Thresholds = thresholds.ToArray(),
    };

    private void OnUsageStateChanged()
    {
        _ = this.Dispatcher.BeginInvoke(() =>
        {
            if (string.Equals(this.activePane, "Providers", StringComparison.Ordinal))
            {
                foreach (var descriptor in this.providers)
                {
                    this.RefreshProviderCard(descriptor.Id);
                }
            }
        });
    }

    private void RefreshProviderCard(ProviderId id)
    {
        if (!this.providerCards.TryGetValue(id, out var card))
        {
            return;
        }

        this.FillProviderCard(card.Descriptor, card.Status, card.Detail);
    }

    private StackPanel Page(string title)
    {
        var panel = new StackPanel
        {
            // Stretch fills the content column (cards span it edge to edge like Win11 Settings);
            // MaxWidth only kicks in on very wide windows, where WPF centers the capped panel.
            MaxWidth = 1000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 24),
        });
        return panel;
    }

    private static StackPanel CardGroup() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private Border SettingCard(string glyph, string title, string? description, FrameworkElement trailing) =>
        this.SettingCard(LogoImages.IconGlyph(glyph, 20), title, description, trailing);

    private Border SettingCard(FrameworkElement leading, string title, string? description, FrameworkElement trailing)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        leading.Width = 20;
        leading.Height = 20;
        leading.VerticalAlignment = VerticalAlignment.Center;
        leading.SetResourceReference(Control.ForegroundProperty, "SettingsForeground");
        Grid.SetColumn(leading, 0);
        content.Children.Add(leading);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            var desc = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
            text.Children.Add(desc);
        }

        Grid.SetColumn(text, 2);
        content.Children.Add(text);

        trailing.VerticalAlignment = VerticalAlignment.Center;
        trailing.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(trailing, 3);
        content.Children.Add(trailing);

        var card = CardBorder();
        card.Child = content;
        return card;
    }

    private Border HeroCard(string version)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var logo = AppLogo();
        Grid.SetColumn(logo, 0);
        grid.Children.Add(logo);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "CodexWinBar", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var versionText = new TextBlock { Text = $"Version {version}", FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        versionText.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
        text.Children.Add(versionText);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var card = CardBorder();
        card.MinHeight = 82;
        card.Child = grid;
        return card;
    }

    private static FrameworkElement AppLogo()
    {
        try
        {
            // app.ico ships as an EmbeddedResource, not a WPF pack resource — decode the stream
            // and pick the largest frame for crisp 48px display.
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .First(static name => name.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(static f => f.PixelWidth).First();
            frame.Freeze();
            return new Image
            {
                Width = 48,
                Height = 48,
                Source = frame,
                Stretch = Stretch.Uniform,
            };
        }
        catch (Exception)
        {
            return LogoImages.IconGlyph(LogoImages.SettingsGlyph, 48);
        }
    }

    private static Border LinkCard(string glyph, string title, string uri)
    {
        var icon = LogoImages.IconGlyph(glyph, 20);
        var titleBlock = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var arrow = LogoImages.IconGlyph(LogoImages.ExternalLinkGlyph, 14);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(titleBlock, 2);
        Grid.SetColumn(arrow, 3);
        grid.Children.Add(icon);
        grid.Children.Add(titleBlock);
        grid.Children.Add(arrow);

        var card = CardBorder();
        card.Cursor = Cursors.Hand;
        card.Child = grid;
        card.MouseLeftButtonUp += (_, _) => OpenUri(uri);
        return card;
    }

    private static Border CardBorder()
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            MinHeight = 64,
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 2),
        };
        border.SetResourceReference(Border.BackgroundProperty, "SettingsCardBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");
        border.BorderThickness = new Thickness(1);
        return border;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 20, 0, 8),
    };

    private static TextBlock Text(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
        return block;
    }

    private static ComboBox Combo<T>(IEnumerable<ComboOption<T>> options, T selected)
    {
        var box = new ComboBox
        {
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.Style = CreateSettingsComboBoxStyle();
        foreach (var option in options)
        {
            box.Items.Add(option);
            if (EqualityComparer<T>.Default.Equals(option.Value, selected))
            {
                box.SelectedItem = option;
            }
        }

        return box;
    }

    private static Style CreateSettingsComboBoxStyle()
    {
        const string xaml = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
       TargetType="{x:Type ComboBox}">
    <Setter Property="Height" Value="32" />
    <Setter Property="Padding" Value="12,0,34,0" />
    <Setter Property="Background" Value="{DynamicResource SettingsControlBackground}" />
    <Setter Property="BorderBrush" Value="{DynamicResource SettingsControlBorder}" />
    <Setter Property="Foreground" Value="{DynamicResource SettingsForeground}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="FontFamily" Value="Segoe UI" />
    <Setter Property="FontSize" Value="14" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
    <Setter Property="ScrollViewer.CanContentScroll" Value="True" />
    <Setter Property="ItemContainerStyle">
        <Setter.Value>
            <Style TargetType="{x:Type ComboBoxItem}">
                <Setter Property="MinHeight" Value="34" />
                <Setter Property="Padding" Value="10,0" />
                <Setter Property="Margin" Value="2,1" />
                <Setter Property="FontFamily" Value="Segoe UI" />
                <Setter Property="FontSize" Value="14" />
                <Setter Property="Foreground" Value="{DynamicResource SettingsForeground}" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="FocusVisualStyle" Value="{x:Null}" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="{x:Type ComboBoxItem}">
                            <Border x:Name="ItemBorder" Background="Transparent" CornerRadius="4" SnapsToDevicePixels="True">
                                <Grid>
                                    <Border x:Name="SelectionPill" Width="3" Height="16" Margin="2,0,0,0" HorizontalAlignment="Left" VerticalAlignment="Center" Background="{DynamicResource SettingsAccent}" CornerRadius="1.5" Visibility="Collapsed" />
                                    <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True" />
                                </Grid>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsHighlighted" Value="True">
                                    <Setter TargetName="ItemBorder" Property="Background" Value="{DynamicResource SettingsSubtleFill}" />
                                </Trigger>
                                <Trigger Property="IsSelected" Value="True">
                                    <Setter TargetName="ItemBorder" Property="Background" Value="{DynamicResource SettingsComboSelectedFill}" />
                                    <Setter TargetName="SelectionPill" Property="Visibility" Value="Visible" />
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter Property="Opacity" Value="0.55" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
        </Setter.Value>
    </Setter>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type ComboBox}">
                <Grid>
                    <ToggleButton x:Name="ToggleButton" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" ClickMode="Press" Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" Padding="{TemplateBinding Padding}">
                        <ToggleButton.Template>
                            <ControlTemplate TargetType="{x:Type ToggleButton}">
                                <Border x:Name="Chrome" Height="32" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" SnapsToDevicePixels="True">
                                    <Grid>
                                        <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Left" VerticalAlignment="Center" Content="{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}" ContentStringFormat="{Binding SelectionBoxItemStringFormat, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}" ContentTemplate="{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}" RecognizesAccessKey="True" />
                                        <TextBlock x:Name="Chevron" Margin="0,0,12,0" HorizontalAlignment="Right" VerticalAlignment="Center" FontFamily="Segoe Fluent Icons" FontSize="10" Foreground="{DynamicResource SettingsMutedForeground}" RenderTransformOrigin="0.5,0.5" Text="&#xE70D;">
                                            <TextBlock.RenderTransform>
                                                <RotateTransform Angle="0" />
                                            </TextBlock.RenderTransform>
                                        </TextBlock>
                                    </Grid>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource SettingsComboHoverFill}" />
                                    </Trigger>
                                    <Trigger Property="IsChecked" Value="True">
                                        <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource SettingsComboHoverFill}" />
                                        <Setter TargetName="Chevron" Property="RenderTransform">
                                            <Setter.Value>
                                                <RotateTransform Angle="180" />
                                            </Setter.Value>
                                        </Setter>
                                    </Trigger>
                                    <Trigger Property="IsEnabled" Value="False">
                                        <Setter TargetName="Chrome" Property="Opacity" Value="0.55" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </ToggleButton.Template>
                    </ToggleButton>
                    <Popup x:Name="PART_Popup" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" VerticalOffset="4" PopupAnimation="Fade">
                        <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" Padding="4" Background="{DynamicResource SettingsComboPopupBackground}" BorderBrush="{DynamicResource SettingsComboPopupBorder}" BorderThickness="1" CornerRadius="8" SnapsToDevicePixels="True">
                            <Border.Effect>
                                <DropShadowEffect BlurRadius="16" Direction="270" Opacity="0.18" ShadowDepth="4" Color="#000000" />
                            </Border.Effect>
                            <ScrollViewer MaxHeight="360" CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                                <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                            </ScrollViewer>
                        </Border>
                    </Popup>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True">
                        <Setter Property="BorderBrush" Value="{DynamicResource SettingsAccent}" />
                    </Trigger>
                    <Trigger Property="HasItems" Value="False">
                        <Setter TargetName="PART_Popup" Property="MinHeight" Value="32" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.55" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
""";

        return (Style)XamlReader.Parse(xaml);
    }

    private static PasswordBox StyledPasswordBox()
    {
        var box = new PasswordBox
        {
            Height = 32,
            MaxWidth = 340,
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 300,
            BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Control.BackgroundProperty, "SettingsControlBackground");
        box.SetResourceReference(Control.BorderBrushProperty, "SettingsControlBorder");
        box.SetResourceReference(Control.ForegroundProperty, "SettingsForeground");
        box.GotKeyboardFocus += (_, _) => box.BorderThickness = new Thickness(1, 1, 1, 2);
        box.LostKeyboardFocus += (_, _) => box.BorderThickness = new Thickness(1);
        return box;
    }

    private static Button Button(string text, Action action, bool accent = false)
    {
        var button = StyledButton(text, accent);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string text, Func<Task> action, bool accent = false)
    {
        var button = StyledButton(text, accent);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Button ButtonWithIcon(string glyph, string text, Action action, bool accent = false)
    {
        var button = Button(text, action, accent);
        button.Content = IconLabel(glyph, text);
        return button;
    }

    private static Button ButtonWithIcon(string glyph, string text, Func<Task> action, bool accent = false)
    {
        var button = Button(text, action, accent);
        button.Content = IconLabel(glyph, text);
        return button;
    }

    private static Button StyledButton(string text, bool accent)
    {
        var button = new Button
        {
            Content = text,
            Height = 32,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
        };
        if (accent)
        {
            button.SetResourceReference(Control.BackgroundProperty, "SettingsAccent");
            button.Foreground = Brushes.White;
            button.SetResourceReference(Control.BorderBrushProperty, "SettingsAccent");
        }
        else
        {
            button.SetResourceReference(Control.BackgroundProperty, "SettingsControlBackground");
            button.SetResourceReference(Control.BorderBrushProperty, "SettingsControlBorder");
            button.SetResourceReference(Control.ForegroundProperty, "SettingsForeground");
        }

        return button;
    }

    private static StackPanel IconLabel(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(LogoImages.IconGlyph(glyph, 13));
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private static bool SystemAppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : true;
    }

    private static Color ReadAccentColor()
    {
        // The user accent is DWM\AccentColor in ABGR layout; ColorizationColor is the window
        // colorization tint (frequently a dark gray) and must not be used for accent fills.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int raw)
        {
            var abgr = unchecked((uint)raw);
            return Color.FromRgb((byte)(abgr & 0xff), (byte)((abgr >> 8) & 0xff), (byte)((abgr >> 16) & 0xff));
        }

        return Color.FromRgb(0x00, 0x67, 0xC0);
    }

    // Instance lookup: the theme dictionary is merged into THIS window's resources, not the
    // Application's — Application.Current.TryFindResource returned null and nulled Foreground.
    private Brush Brush(string key) => (Brush)this.FindResource(key);

    private sealed record ComboOption<T>(string Label, T Value)
    {
        public override string ToString() => this.Label;
    }

    private sealed class NavRow : Border
    {
        private readonly Border indicator;
        private readonly Border fill;
        private bool isSelected;
        private bool isHover;

        public NavRow(string label, string glyph, Action click)
        {
            this.Label = label;
            this.Height = 36;
            this.CornerRadius = new CornerRadius(4);

            this.fill = new Border { CornerRadius = new CornerRadius(4), Opacity = 0 };
            this.fill.SetResourceReference(Border.BackgroundProperty, "SettingsSubtleFill");

            this.indicator = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            this.indicator.SetResourceReference(Border.BackgroundProperty, "SettingsAccent");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Margin = new Thickness(12, 0, 12, 0);
            var icon = LogoImages.IconGlyph(glyph, 16);
            icon.SetResourceReference(Control.ForegroundProperty, "SettingsForeground");
            var text = new TextBlock
            {
                Text = label,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(text, 2);
            row.Children.Add(icon);
            row.Children.Add(text);

            var grid = new Grid();
            grid.Children.Add(this.fill);
            grid.Children.Add(row);
            grid.Children.Add(this.indicator);
            this.Child = grid;

            this.MouseEnter += (_, _) =>
            {
                this.isHover = true;
                this.UpdateVisual();
            };
            this.MouseLeave += (_, _) =>
            {
                this.isHover = false;
                this.UpdateVisual();
            };
            this.MouseLeftButtonUp += (_, _) => click();
        }

        public string Label { get; }

        public bool IsSelected
        {
            get => this.isSelected;
            set
            {
                this.isSelected = value;
                this.UpdateVisual();
            }
        }

        private void UpdateVisual()
        {
            this.fill.Opacity = this.isSelected ? 1 : this.isHover ? 0.62 : 0;
            this.indicator.Opacity = this.isSelected ? 1 : 0;
        }
    }

    private sealed class ToggleSwitch : ToggleButton
    {
        private readonly TranslateTransform thumbTransform = new();
        private readonly TextBlock label = new();
        private readonly Border track = new();
        private readonly Border thumb = new();
        private readonly Action<bool> changed;

        public ToggleSwitch(bool isChecked, Action<bool> changed)
        {
            this.changed = changed;
            this.IsChecked = isChecked;
            this.Focusable = true;
            this.Cursor = Cursors.Hand;
            this.Background = Brushes.Transparent;
            this.BorderThickness = new Thickness(0);

            // Strip the default ToggleButton chrome (its hover/checked highlight painted
            // behind our custom track); render only our content.
            var bareTemplate = new ControlTemplate(typeof(ToggleButton));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            bareTemplate.VisualTree = presenter;
            this.Template = bareTemplate;

            this.Content = this.BuildContent();
            this.Checked += (_, _) => this.SetState(true, true);
            this.Unchecked += (_, _) => this.SetState(false, true);
            this.Loaded += (_, _) => this.SetState(this.IsChecked == true, false);
        }

        private UIElement BuildContent()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            this.label.FontSize = 14;
            this.label.Width = 24;
            this.label.VerticalAlignment = VerticalAlignment.Center;
            this.label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsForeground");

            this.track.Width = 40;
            this.track.Height = 20;
            this.track.CornerRadius = new CornerRadius(10);
            this.track.BorderThickness = new Thickness(1);
            this.track.Margin = new Thickness(12, 0, 0, 0);

            var grid = new Grid();
            this.thumb.Width = 12;
            this.thumb.Height = 12;
            this.thumb.CornerRadius = new CornerRadius(6);
            this.thumb.HorizontalAlignment = HorizontalAlignment.Left;
            this.thumb.VerticalAlignment = VerticalAlignment.Center;
            this.thumb.Margin = new Thickness(4, 0, 0, 0);
            this.thumb.RenderTransform = this.thumbTransform;
            grid.Children.Add(this.thumb);
            this.track.Child = grid;

            panel.Children.Add(this.label);
            panel.Children.Add(this.track);
            return panel;
        }

        public void SetChecked(bool on, bool notify)
        {
            this.IsChecked = on;
            this.SetState(on, notify);
        }

        private void SetState(bool on, bool notify)
        {
            this.label.Text = on ? "On" : "Off";
            if (on)
            {
                this.track.SetResourceReference(Border.BackgroundProperty, "SettingsAccent");
                this.track.SetResourceReference(Border.BorderBrushProperty, "SettingsAccent");
                this.thumb.Background = Brushes.White;
            }
            else
            {
                this.track.Background = Brushes.Transparent;
                this.track.SetResourceReference(Border.BorderBrushProperty, "SettingsSwitchOffBorder");
                this.thumb.SetResourceReference(Border.BackgroundProperty, "SettingsSwitchThumb");
            }

            var animation = new DoubleAnimation(on ? 20 : 0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            this.thumbTransform.BeginAnimation(TranslateTransform.XProperty, animation);
            if (notify)
            {
                this.changed(on);
            }
        }
    }

    private static TextBlock CreateExpanderChevron()
    {
        var chevron = LogoImages.IconGlyph("\uE70D", 12);
        chevron.Margin = new Thickness(12, 0, 0, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(0);
        return chevron;
    }

    private sealed class ExpandableCardAnimator
    {
        private readonly Border owner;
        private readonly FrameworkElement drawer;
        private readonly TextBlock chevron;
        private readonly UIElement? divider;
        private int animationGeneration;

        public ExpandableCardAnimator(Border owner, FrameworkElement drawer, TextBlock chevron, UIElement? divider)
        {
            this.owner = owner;
            this.drawer = drawer;
            this.chevron = chevron;
            this.divider = divider;
            this.drawer.ClipToBounds = true;
            this.drawer.Visibility = Visibility.Collapsed;
            this.drawer.Opacity = 0;
            if (this.divider is not null)
            {
                this.divider.Visibility = Visibility.Collapsed;
            }
        }

        public void SetExpanded(bool expanded)
        {
            var generation = ++this.animationGeneration;
            this.AnimateChevron(expanded);
            if (expanded)
            {
                this.Expand(generation);
            }
            else
            {
                this.Collapse(generation);
            }
        }

        private void AnimateChevron(bool expanded)
        {
            if (this.chevron.RenderTransform is not RotateTransform rotate)
            {
                rotate = new RotateTransform(0);
                this.chevron.RenderTransform = rotate;
            }

            var animation = new DoubleAnimation
            {
                To = expanded ? 180 : 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void Expand(int generation)
        {
            this.divider?.SetCurrentValue(VisibilityProperty, Visibility.Visible);
            this.drawer.Visibility = Visibility.Visible;

            var startHeight = Math.Max(0, this.drawer.ActualHeight);
            this.drawer.Height = startHeight;
            this.drawer.Measure(new Size(this.InnerWidth(), double.PositiveInfinity));
            var targetHeight = Math.Max(0, this.drawer.DesiredSize.Height);

            var heightAnimation = new DoubleAnimation
            {
                From = startHeight,
                To = targetHeight,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            heightAnimation.Completed += (_, _) =>
            {
                if (generation != this.animationGeneration)
                {
                    return;
                }

                this.drawer.BeginAnimation(FrameworkElement.HeightProperty, null);
                this.drawer.Height = double.NaN;
            };

            this.drawer.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
            this.drawer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = this.drawer.Opacity,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
            });
        }

        private void Collapse(int generation)
        {
            var startHeight = Math.Max(0, this.drawer.ActualHeight);
            this.drawer.Height = startHeight;

            var heightAnimation = new DoubleAnimation
            {
                From = startHeight,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            heightAnimation.Completed += (_, _) =>
            {
                if (generation != this.animationGeneration)
                {
                    return;
                }

                this.drawer.BeginAnimation(FrameworkElement.HeightProperty, null);
                this.drawer.Visibility = Visibility.Collapsed;
                this.drawer.Height = double.NaN;
                this.divider?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            };

            this.drawer.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
            this.drawer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = this.drawer.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(120),
            });
        }

        private double InnerWidth()
        {
            var width = this.owner.ActualWidth - this.owner.Padding.Left - this.owner.Padding.Right;
            if (double.IsNaN(width) || width <= 0)
            {
                width = this.drawer.ActualWidth;
            }

            return Math.Max(0, width);
        }
    }

    private sealed class QuotaSettingsCard : Border
    {
        private readonly Border divider = new();
        private readonly ExpandableCardAnimator expander;
        private bool expanded;

        public QuotaSettingsCard(FrameworkElement leading, ToggleSwitch toggle, StackPanel detail)
        {
            this.CornerRadius = new CornerRadius(4);
            this.MinHeight = 64;
            this.Padding = new Thickness(16, 14, 16, 14);
            this.Margin = new Thickness(0, 0, 0, 2);
            this.BorderThickness = new Thickness(1);
            this.Cursor = Cursors.Hand;
            this.SetResourceReference(Border.BackgroundProperty, "SettingsCardBackground");
            this.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");

            var root = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

            leading.Width = 20;
            leading.Height = 20;
            leading.VerticalAlignment = VerticalAlignment.Center;
            leading.SetResourceReference(Control.ForegroundProperty, "SettingsForeground");
            Grid.SetColumn(leading, 0);
            row.Children.Add(leading);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = "Quota notifications",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            var desc = new TextBlock
            {
                Text = "Notify when a usage window crosses a threshold",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
            text.Children.Add(desc);
            Grid.SetColumn(text, 2);
            row.Children.Add(text);

            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            var chevron = CreateExpanderChevron();
            Grid.SetColumn(chevron, 4);
            row.Children.Add(chevron);

            root.Children.Add(row);
            this.divider.Height = 1;
            this.divider.Margin = new Thickness(40, 14, 0, 0);
            this.divider.SetResourceReference(Border.BackgroundProperty, "SettingsDivider");
            root.Children.Add(this.divider);
            root.Children.Add(detail);
            this.Child = root;
            this.expander = new ExpandableCardAnimator(this, detail, chevron, this.divider);

            this.MouseLeftButtonUp += (_, args) =>
            {
                if (args.OriginalSource is DependencyObject source && IsInsideInteractiveControl(source))
                {
                    return;
                }

                this.IsExpanded = !this.IsExpanded;
            };
        }

        public bool IsExpanded
        {
            get => this.expanded;
            set
            {
                this.expanded = value;
                this.expander.SetExpanded(value);
            }
        }

        private static bool IsInsideInteractiveControl(DependencyObject source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is ToggleSwitch or ButtonBase or TextBox or ThresholdTrack)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }
    }

    private sealed class QuotaWarningsEditor(SettingsWindow owner)
    {
        private const string DefaultScope = "__default";
        private readonly TextBlock status = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        private QuotaWindowControls? sessionControls;
        private QuotaWindowControls? weeklyControls;
        private ProviderId? selectedProvider;
        private bool refreshing;

        public StackPanel Build(UiSettings settings)
        {
            this.status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
            var detail = new StackPanel { Margin = new Thickness(40, 14, 0, 0) };
            detail.Children.Add(this.CreateApplyToRow());
            detail.Children.Add(this.status);
            this.sessionControls = this.CreateWindowRow(
                "5-hour window",
                "Alert when remaining drops below this",
                isSession: true);
            detail.Children.Add(this.sessionControls.Root);
            this.weeklyControls = this.CreateWindowRow(
                "Weekly window",
                "Alert when remaining drops below this",
                isSession: false);
            detail.Children.Add(this.weeklyControls.Root);
            this.Refresh(settings);
            return detail;
        }

        private FrameworkElement CreateApplyToRow()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = "Apply to",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var options = new List<ComboOption<string>>
            {
                new("All providers (default)", DefaultScope),
            };
            options.AddRange(owner.providers.Select(provider =>
                new ComboOption<string>(provider.Metadata.DisplayName, provider.Id.ConfigId())));
            var combo = Combo(options, DefaultScope);
            combo.MinWidth = 240;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not ComboOption<string> option)
                {
                    return;
                }

                this.selectedProvider = string.Equals(option.Value, DefaultScope, StringComparison.Ordinal)
                    ? null
                    : ProviderIds.All.First(id => string.Equals(id.ConfigId(), option.Value, StringComparison.OrdinalIgnoreCase));
                this.Refresh(owner.uiStore.Load());
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            return row;
        }

        private QuotaWindowControls CreateWindowRow(string title, string description, bool isSession)
        {
            var root = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            var desc = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedForeground");
            text.Children.Add(desc);
            Grid.SetColumn(text, 0);
            root.Children.Add(text);

            ToggleSwitch? toggle = null;
            var track = new ThresholdTrack
            {
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            toggle = new ToggleSwitch(true, enabled =>
            {
                if (this.refreshing)
                {
                    return;
                }

                track.IsEnabled = enabled;
                this.Commit(isSession, enabled, track.Values);
            });
            Grid.SetColumn(toggle, 1);
            root.Children.Add(toggle);

            Grid.SetRow(track, 1);
            Grid.SetColumnSpan(track, 2);
            root.Children.Add(track);
            track.ValuesChanged += values =>
            {
                this.Commit(isSession, toggle.IsChecked == true, values);
            };
            return new QuotaWindowControls(root, toggle, track);
        }

        private void Refresh(UiSettings settings)
        {
            if (this.sessionControls is null || this.weeklyControls is null)
            {
                return;
            }

            this.refreshing = true;
            try
            {
                var quotaWarnings = this.SelectedQuotaWarnings();
                this.status.Inlines.Clear();
                if (this.selectedProvider is null || quotaWarnings is null)
                {
                    this.status.Text = "Using default thresholds";
                }
                else
                {
                    var name = owner.providers.First(provider => provider.Id == this.selectedProvider.Value).Metadata.DisplayName;
                    this.status.Text = string.Empty;
                    this.status.Inlines.Add(new Run($"Custom thresholds for {name} "));
                    var reset = new Hyperlink(new Run("Reset to defaults"));
                    reset.Click += (_, _) =>
                    {
                        owner.ResetQuotaProviderOverride(this.selectedProvider.Value);
                        this.Refresh(owner.uiStore.Load());
                    };
                    this.status.Inlines.Add(reset);
                }

                this.ApplyWindow(
                    this.sessionControls,
                    quotaWarnings?.Session,
                    settings.QuotaSessionEnabled,
                    settings.QuotaSessionThresholds);
                this.ApplyWindow(
                    this.weeklyControls,
                    quotaWarnings?.Weekly,
                    settings.QuotaWeeklyEnabled,
                    settings.QuotaWeeklyThresholds);
            }
            finally
            {
                this.refreshing = false;
            }
        }

        private void ApplyWindow(
            QuotaWindowControls controls,
            QuotaWarningWindow? overrideWindow,
            bool defaultEnabled,
            IReadOnlyList<int> defaultThresholds)
        {
            var enabled = overrideWindow?.Enabled ?? defaultEnabled;
            var thresholds = overrideWindow is null
                ? defaultThresholds
                : overrideWindow.Thresholds ?? [];
            controls.Toggle.SetChecked(enabled, notify: false);
            controls.Track.Values = NormalizeThresholds(thresholds);
            controls.Track.IsEnabled = enabled;
        }

        private QuotaWarnings? SelectedQuotaWarnings()
        {
            if (this.selectedProvider is null)
            {
                return null;
            }

            var config = owner.configStore.Load();
            return owner.configStore.EntryFor(config, this.selectedProvider.Value).QuotaWarnings;
        }

        private void Commit(bool isSession, bool enabled, IReadOnlyList<int> values)
        {
            if (this.refreshing)
            {
                return;
            }

            var thresholds = NormalizeThresholds(values);
            if (this.selectedProvider is null)
            {
                owner.SaveUi(ui =>
                {
                    if (isSession)
                    {
                        ui.QuotaSessionEnabled = enabled;
                        ui.QuotaSessionThresholds = thresholds;
                    }
                    else
                    {
                        ui.QuotaWeeklyEnabled = enabled;
                        ui.QuotaWeeklyThresholds = thresholds;
                    }
                });
            }
            else
            {
                owner.SaveQuotaProviderOverride(this.selectedProvider.Value, isSession, enabled, thresholds);
            }

            this.Refresh(owner.uiStore.Load());
        }

        private static IReadOnlyList<int> NormalizeThresholds(IEnumerable<int> values) =>
            values
                .Select(value => Math.Clamp(value, 0, 99))
                .Distinct()
                .OrderDescending()
                .ToArray();

        private sealed record QuotaWindowControls(Grid Root, ToggleSwitch Toggle, ThresholdTrack Track);
    }

    private sealed class ProviderCard : Border
    {
        private readonly Border divider = new();
        private readonly ExpandableCardAnimator expander;
        private bool expanded;

        public ProviderCard(
            ProviderDescriptor descriptor,
            FrameworkElement logo,
            TextBlock status,
            ToggleSwitch toggle,
            StackPanel detail,
            bool canExpand)
        {
            this.Descriptor = descriptor;
            this.Status = status;
            this.Detail = detail;
            this.CornerRadius = new CornerRadius(4);
            this.MinHeight = 64;
            this.Padding = new Thickness(16, 14, 16, 14);
            this.Margin = new Thickness(0, 0, 0, 2);
            this.BorderThickness = new Thickness(1);
            this.SetResourceReference(Border.BackgroundProperty, "SettingsCardBackground");
            this.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");

            var root = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(canExpand ? 28 : 0) });

            Grid.SetColumn(logo, 0);
            row.Children.Add(logo);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = descriptor.Metadata.DisplayName,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            status.FontSize = 12;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(status);
            Grid.SetColumn(text, 2);
            row.Children.Add(text);

            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            var chevron = CreateExpanderChevron();
            chevron.Visibility = canExpand ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(chevron, 4);
            row.Children.Add(chevron);

            root.Children.Add(row);
            this.divider.Height = 1;
            this.divider.Margin = new Thickness(40, 14, 0, 0);
            this.divider.SetResourceReference(Border.BackgroundProperty, "SettingsDivider");
            root.Children.Add(this.divider);
            root.Children.Add(detail);
            this.Child = root;
            this.expander = new ExpandableCardAnimator(this, detail, chevron, this.divider);

            if (canExpand)
            {
                this.Cursor = Cursors.Hand;
                this.MouseLeftButtonUp += (_, args) =>
                {
                    if (args.OriginalSource is DependencyObject source && IsInsideToggle(source))
                    {
                        return;
                    }

                    this.IsExpanded = !this.IsExpanded;
                };
            }
        }

        public ProviderDescriptor Descriptor { get; }
        public TextBlock Status { get; }
        public StackPanel Detail { get; }

        public bool IsExpanded
        {
            get => this.expanded;
            set
            {
                this.expanded = value;
                this.expander.SetExpanded(value);
            }
        }

        private static bool IsInsideToggle(DependencyObject source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is ToggleSwitch)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }
    }

    private static class SettingsTheme
    {
        public static ResourceDictionary Create(bool dark, Color accent)
        {
            var dict = new ResourceDictionary
            {
                ["SettingsAccent"] = new SolidColorBrush(accent),
                ["SettingsForeground"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A)),
                ["SettingsMutedForeground"] = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xff, 0xff, 0xff) : Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                ["SettingsCardBackground"] = new SolidColorBrush(dark ? Color.FromArgb(0x0f, 0xff, 0xff, 0xff) : Color.FromArgb(0xb3, 0xff, 0xff, 0xff)),
                ["SettingsCardBorder"] = new SolidColorBrush(dark ? Color.FromArgb(0x26, 0x00, 0x00, 0x00) : Color.FromArgb(0x19, 0x00, 0x00, 0x00)),
                ["SettingsSubtleFill"] = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xff, 0xff, 0xff) : Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
                ["SettingsControlBackground"] = new SolidColorBrush(dark ? Color.FromArgb(0x0f, 0xff, 0xff, 0xff) : Color.FromArgb(0xf2, 0xff, 0xff, 0xff)),
                ["SettingsControlBorder"] = new SolidColorBrush(dark ? Color.FromArgb(0x26, 0xff, 0xff, 0xff) : Color.FromArgb(0x26, 0x00, 0x00, 0x00)),
                ["SettingsComboHoverFill"] = new SolidColorBrush(dark ? Color.FromArgb(0x1f, 0xff, 0xff, 0xff) : Color.FromArgb(0xff, 0xf3, 0xf3, 0xf3)),
                ["SettingsComboSelectedFill"] = new SolidColorBrush(dark ? Color.FromArgb(0x29, 0xff, 0xff, 0xff) : Color.FromArgb(0xff, 0xee, 0xee, 0xee)),
                ["SettingsComboPopupBackground"] = new SolidColorBrush(dark ? Color.FromRgb(0x2c, 0x2c, 0x2c) : Color.FromRgb(0xf9, 0xf9, 0xf9)),
                ["SettingsComboPopupBorder"] = new SolidColorBrush(dark ? Color.FromArgb(0x1f, 0xff, 0xff, 0xff) : Color.FromArgb(0x1f, 0x00, 0x00, 0x00)),
                ["SettingsDivider"] = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xff, 0xff, 0xff) : Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
                ["SettingsSwitchOffBorder"] = new SolidColorBrush(dark ? Color.FromArgb(0x73, 0xff, 0xff, 0xff) : Color.FromArgb(0x73, 0x00, 0x00, 0x00)),
                ["SettingsSwitchThumb"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x33, 0x33, 0x33)),
            };

            foreach (var brush in dict.Values.OfType<SolidColorBrush>())
            {
                brush.Freeze();
            }

            return dict;
        }
    }
}
