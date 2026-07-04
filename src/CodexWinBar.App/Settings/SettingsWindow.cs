using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
    private static SettingsWindow? current;

    private readonly ConfigStore configStore;
    private readonly UiSettingsStore uiStore;
    private readonly IUsageStore usageStore;
    private readonly Action applySettings;
    private readonly IReadOnlyList<ProviderDescriptor> providers;
    private readonly Grid contentHost = new();
    private readonly ListBox providerList = new();
    private readonly TextBlock providerStatus = new();
    private readonly StackPanel providerDetail = new() { Margin = new Thickness(18, 0, 0, 0) };
    private readonly HttpClient http = new();
    private CancellationTokenSource? copilotSignIn;

    private SettingsWindow(ConfigStore cfg, UiSettingsStore ui, IUsageStore store, Action applySettings)
    {
        this.configStore = cfg;
        this.uiStore = ui;
        this.usageStore = store;
        this.applySettings = applySettings;
        this.providers = ProviderCatalog.CreateAll();

        this.Title = "CodexWinBar Settings";
        this.Width = 720;
        this.Height = 520;
        this.MinWidth = 680;
        this.MinHeight = 480;
        this.WindowStyle = WindowStyle.SingleBorderWindow;
        this.ShowInTaskbar = true;
        this.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        this.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        this.Foreground = Brushes.White;
        this.Content = this.BuildRoot();

        this.SourceInitialized += (_, _) => WpfDwm.ApplyWindowChrome(this, dark: true);
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

        this.ShowGeneral();
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

    private Grid BuildRoot()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(27, 27, 27)),
            Foreground = Brushes.White,
            Padding = new Thickness(8, 16, 8, 8),
        };
        foreach (var name in new[] { "General", "Display", "Providers", "About" })
        {
            nav.Items.Add(new ListBoxItem { Content = name, Padding = new Thickness(12, 9, 12, 9) });
        }

        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedItem is not ListBoxItem item || item.Content is not string name)
            {
                return;
            }

            switch (name)
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
        };
        nav.SelectedIndex = 0;
        Grid.SetColumn(nav, 0);
        root.Children.Add(nav);

        var scroller = new ScrollViewer
        {
            Content = this.contentHost,
            Padding = new Thickness(24),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);
        return root;
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

        var launch = Check("Launch at login", StartupManager.IsEnabled());
        launch.Checked += (_, _) =>
        {
            StartupManager.SetEnabled(true);
            this.SaveUi(ui => ui with { LaunchAtLogin = true });
        };
        launch.Unchecked += (_, _) =>
        {
            StartupManager.SetEnabled(false);
            this.SaveUi(ui => ui with { LaunchAtLogin = false });
        };
        panel.Children.Add(launch);

        panel.Children.Add(Label("Refresh cadence"));
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
                this.SaveUi(ui => ui with { RefreshCadenceMinutes = option.Value });
            }
        };
        panel.Children.Add(cadence);

        var status = Check("Check provider status", settings.StatusChecksEnabled);
        status.Checked += (_, _) => this.SaveUi(ui => ui with { StatusChecksEnabled = true });
        status.Unchecked += (_, _) => this.SaveUi(ui => ui with { StatusChecksEnabled = false });
        panel.Children.Add(status);

        var notifications = Check("Quota notifications", settings.QuotaNotificationsEnabled);
        notifications.Checked += (_, _) => this.SaveUi(ui => ui with { QuotaNotificationsEnabled = true });
        notifications.Unchecked += (_, _) => this.SaveUi(ui => ui with { QuotaNotificationsEnabled = false });
        panel.Children.Add(notifications);

        this.SetContent(panel);
    }

    private void ShowDisplay()
    {
        var settings = this.uiStore.Load();
        var panel = Page("Display");

        panel.Children.Add(Label("Widget mode"));
        var mode = Combo(Enum.GetValues<WidgetMode>().Select(value => new ComboOption<WidgetMode>(value.ToString(), value)), settings.WidgetMode);
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedItem is ComboOption<WidgetMode> option)
            {
                this.SaveUi(ui => ui with { WidgetMode = option.Value });
            }
        };
        panel.Children.Add(mode);

        panel.Children.Add(Label("Widget side"));
        var side = Combo(
            new[]
            {
                new ComboOption<WidgetSide>("Right", WidgetSide.Right),
                new ComboOption<WidgetSide>("Left (forces overlay)", WidgetSide.Left),
            },
            settings.WidgetSide);
        side.SelectionChanged += (_, _) =>
        {
            if (side.SelectedItem is ComboOption<WidgetSide> option)
            {
                this.SaveUi(ui => ui with
                {
                    WidgetSide = option.Value,
                    WidgetMode = option.Value == WidgetSide.Left ? WidgetMode.Overlay : ui.WidgetMode,
                });
            }
        };
        panel.Children.Add(side);

        panel.Children.Add(Label("Display text mode"));
        var textMode = Combo(Enum.GetValues<DisplayTextMode>().Select(value => new ComboOption<DisplayTextMode>(value.ToString(), value)), settings.DisplayTextMode);
        textMode.SelectionChanged += (_, _) =>
        {
            if (textMode.SelectedItem is ComboOption<DisplayTextMode> option)
            {
                this.SaveUi(ui => ui with { DisplayTextMode = option.Value });
            }
        };
        panel.Children.Add(textMode);

        var used = Check("Show used instead of remaining", settings.UsageBarsShowUsed);
        used.Checked += (_, _) => this.SaveUi(ui => ui with { UsageBarsShowUsed = true });
        used.Unchecked += (_, _) => this.SaveUi(ui => ui with { UsageBarsShowUsed = false });
        panel.Children.Add(used);

        var absolute = Check("Absolute reset times", settings.ResetTimesShowAbsolute);
        absolute.Checked += (_, _) => this.SaveUi(ui => ui with { ResetTimesShowAbsolute = true });
        absolute.Unchecked += (_, _) => this.SaveUi(ui => ui with { ResetTimesShowAbsolute = false });
        panel.Children.Add(absolute);

        this.SetContent(panel);
    }

    private void ShowProviders()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        this.providerList.Items.Clear();
        this.providerList.BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70));
        this.providerList.Background = new SolidColorBrush(Color.FromRgb(37, 37, 37));
        this.providerList.Foreground = Brushes.White;
        foreach (var descriptor in this.providers)
        {
            this.providerList.Items.Add(this.ProviderRow(descriptor));
        }

        this.providerList.SelectionChanged -= this.ProviderSelectionChanged;
        this.providerList.SelectionChanged += this.ProviderSelectionChanged;
        this.providerList.SelectedIndex = 0;
        Grid.SetColumn(this.providerList, 0);
        root.Children.Add(this.providerList);

        Grid.SetColumn(this.providerDetail, 1);
        root.Children.Add(this.providerDetail);
        this.SetContent(root);
        this.RefreshProviderDetail();
    }

    private FrameworkElement ProviderRow(ProviderDescriptor descriptor)
    {
        var entry = this.configStore.EntryFor(this.configStore.Load(), descriptor.Id);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6), Tag = descriptor };
        row.Children.Add(new TextBlock { Text = descriptor.Branding.GlyphKey, Width = 32, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = descriptor.Metadata.DisplayName, Width = 118, VerticalAlignment = VerticalAlignment.Center });
        var check = new CheckBox { IsChecked = entry.Enabled ?? descriptor.Metadata.DefaultEnabled, VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => this.SaveProviderEntry(descriptor.Id, item => item with { Enabled = true });
        check.Unchecked += (_, _) => this.SaveProviderEntry(descriptor.Id, item => item with { Enabled = false });
        row.Children.Add(check);
        return row;
    }

    private void ProviderSelectionChanged(object sender, SelectionChangedEventArgs e) => this.RefreshProviderDetail();

    private void RefreshProviderDetail()
    {
        this.providerDetail.Children.Clear();
        if (this.providerList.SelectedItem is not StackPanel { Tag: ProviderDescriptor descriptor })
        {
            return;
        }

        this.providerDetail.Children.Add(Header(descriptor.Metadata.DisplayName));
        this.providerStatus.Text = this.AuthStateText(descriptor.Id);
        this.providerStatus.Margin = new Thickness(0, 0, 0, 14);
        this.providerStatus.TextWrapping = TextWrapping.Wrap;
        this.providerDetail.Children.Add(this.providerStatus);

        if (descriptor.Id is ProviderId.OpenRouter or ProviderId.Zai or ProviderId.OpenAIAdmin)
        {
            this.AddApiKeyEditor(descriptor);
        }
        else if (descriptor.Id == ProviderId.Copilot)
        {
            this.AddCopilotEditor();
        }
        else
        {
            this.providerDetail.Children.Add(Text(this.CredentialHint(descriptor.Id)));
            this.providerDetail.Children.Add(Button("Refresh now", async () => await this.RefreshProviderAsync(descriptor.Id)));
        }
    }

    private void AddApiKeyEditor(ProviderDescriptor descriptor)
    {
        var entry = this.configStore.EntryFor(this.configStore.Load(), descriptor.Id);
        this.providerDetail.Children.Add(Text(string.IsNullOrWhiteSpace(entry.ApiKey) ? "No API key saved." : "API key saved: •••"));
        var password = new PasswordBox { Margin = new Thickness(0, 8, 0, 8), MaxWidth = 300 };
        this.providerDetail.Children.Add(password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("Save", () =>
        {
            this.SaveProviderEntry(descriptor.Id, item => item with { ApiKey = password.Password, Source = "api" });
            password.Clear();
            this.RefreshProviderDetail();
        }));
        buttons.Children.Add(Button("Clear", () =>
        {
            this.SaveProviderEntry(descriptor.Id, item => item with { ApiKey = null });
            this.RefreshProviderDetail();
        }));
        this.providerDetail.Children.Add(buttons);
        this.providerDetail.Children.Add(Button("Refresh now", async () => await this.RefreshProviderAsync(descriptor.Id)));
    }

    private void AddCopilotEditor()
    {
        this.providerDetail.Children.Add(Text("Sign in with GitHub device flow. The token is stored in the Copilot apiKey field."));
        this.providerDetail.Children.Add(Button("Sign in with GitHub", async () => await this.StartCopilotSignInAsync()));
        this.providerDetail.Children.Add(Button("Refresh now", async () => await this.RefreshProviderAsync(ProviderId.Copilot)));
    }

    private async Task StartCopilotSignInAsync()
    {
        this.copilotSignIn?.Cancel();
        this.copilotSignIn?.Dispose();
        this.copilotSignIn = new CancellationTokenSource();
        var ct = this.copilotSignIn.Token;

        try
        {
            this.providerStatus.Text = "Starting GitHub device flow...";
            var info = await CopilotAuth.StartAsync(this.http, ct);
            this.providerDetail.Children.Add(Header(info.UserCode));
            this.providerDetail.Children.Add(Button("Copy code", () => Clipboard.SetText(info.UserCode)));
            this.providerDetail.Children.Add(Button("Open GitHub", () => OpenUri(info.VerificationUri)));
            this.providerDetail.Children.Add(Button("Cancel", () => this.copilotSignIn?.Cancel()));
            OpenUri(info.VerificationUri);
            this.providerStatus.Text = $"Waiting for GitHub authorization. Expires at {info.ExpiresAt.LocalDateTime:t}.";
            var token = await CopilotAuth.PollAsync(this.http, info, ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                this.providerStatus.Text = "GitHub authorization expired or was denied.";
                return;
            }

            this.SaveProviderEntry(ProviderId.Copilot, entry => entry with { ApiKey = token, Source = "oauth" });
            this.providerStatus.Text = "GitHub authorization complete.";
            await this.RefreshProviderAsync(ProviderId.Copilot);
        }
        catch (OperationCanceledException)
        {
            this.providerStatus.Text = "GitHub authorization cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            this.providerStatus.Text = ex.Message;
        }
    }

    private string AuthStateText(ProviderId id)
    {
        var state = this.usageStore.States.FirstOrDefault(item => item.Provider == id);
        if (state is null)
        {
            return "No usage state yet.";
        }

        if (state.NeedsAuthentication)
        {
            return "Needs authentication.";
        }

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            return $"Stale: {state.Error}";
        }

        var identity = state.Snapshot?.Identity;
        var name = identity?.AccountEmail ?? identity?.AccountOrganization ?? identity?.Plan;
        return string.IsNullOrWhiteSpace(name) ? "OK." : $"OK: {name}";
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
            this.providerStatus.Text = ex.Message;
        }
    }

    private void ShowAbout()
    {
        var panel = Page("About");
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        panel.Children.Add(Text($"Version {version}"));
        panel.Children.Add(LinkText("Windows-native fork of steipete/CodexBar (MIT)", "https://github.com/steipete/CodexBar"));
        panel.Children.Add(LinkText("CodexWinBar repository", "https://github.com/steipete/CodexWinBar"));
        panel.Children.Add(Text("MIT license. Provider credentials remain in upstream-compatible local config files."));
        this.SetContent(panel);
    }

    private void SaveUi(Func<UiSettings, UiSettings> update)
    {
        this.uiStore.Save(update(this.uiStore.Load()));
        this.applySettings();
    }

    private void SaveProviderEntry(ProviderId id, Func<ProviderConfigEntry, ProviderConfigEntry> update)
    {
        var config = this.configStore.Load();
        var entry = this.configStore.EntryFor(config, id);
        this.configStore.Save(this.configStore.WithEntry(config, update(entry)));
        this.applySettings();
    }

    private void OnUsageStateChanged()
    {
        _ = this.Dispatcher.BeginInvoke(() =>
        {
            if (this.providerList.IsVisible)
            {
                this.RefreshProviderDetail();
            }
        });
    }

    private static StackPanel Page(string title)
    {
        var panel = new StackPanel { MaxWidth = 460 };
        panel.Children.Add(Header(title));
        return panel;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 18),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 12, 0, 4),
        Opacity = 0.82,
    };

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 10),
    };

    private static CheckBox Check(string text, bool isChecked) => new()
    {
        Content = text,
        IsChecked = isChecked,
        Margin = new Thickness(0, 8, 0, 8),
    };

    private static ComboBox Combo<T>(IEnumerable<ComboOption<T>> options, T selected)
    {
        var box = new ComboBox { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
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

    private static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 8, 8, 8),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 8, 8, 8),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static TextBlock LinkText(string text, string uri)
    {
        var block = Text(string.Empty);
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(uri) };
        link.RequestNavigate += (_, args) => OpenUri(args.Uri.ToString());
        block.Inlines.Add(link);
        return block;
    }

    private static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private sealed record ComboOption<T>(string Label, T Value)
    {
        public override string ToString() => this.Label;
    }
}
