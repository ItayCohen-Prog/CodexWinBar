using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using CodexWinBar.App.Assets;
using CodexWinBar.Core.Auth;
using CodexWinBar.Core.Config;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;
using CodexWinBar.Providers.Claude;
using CodexWinBar.Providers.Codex;
using CodexWinBar.Providers.Copilot;
using CodexWinBar.Providers.Gemini;

namespace CodexWinBar.App.Settings;

/// <summary>
/// First-run welcome window that invites the user to connect the AI providers they use. Every
/// provider is set up in place: Codex, Claude and Gemini sign in through the browser; Copilot uses
/// the GitHub device flow; the rest take an API key or cookie inline. Nothing is connected until the
/// user acts, and connecting a provider enables it.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private static OnboardingWindow? current;

    private readonly IReadOnlyList<ProviderDescriptor> providers;
    private readonly ConfigStore configStore;
    private readonly UiSettingsStore uiStore;
    private readonly IUsageStore usageStore;
    private readonly Action applySettings;
    private readonly AppCredentialStore credentialStore = new(Environment.GetEnvironmentVariable);
    private readonly HttpClient http = new();
    private readonly bool isDark;
    private readonly ControlTemplate buttonTemplate;

    // One sign-in per provider: a new sign-in cancels only that provider's prior attempt, so
    // connecting Codex does not abort an in-flight Gemini sign-in.
    private readonly Dictionary<ProviderId, CancellationTokenSource> signIns = [];

    private OnboardingWindow(
        IReadOnlyList<ProviderDescriptor> providers,
        ConfigStore configStore,
        UiSettingsStore uiStore,
        IUsageStore usageStore,
        Action applySettings)
    {
        this.providers = providers;
        this.configStore = configStore;
        this.uiStore = uiStore;
        this.usageStore = usageStore;
        this.applySettings = applySettings;
        this.isDark = !SystemAppsUseLightTheme();
        this.buttonTemplate = BuildButtonTemplate(this.isDark);

        this.Title = "Welcome to CodexWinBar";
        this.Width = 580;
        this.SizeToContent = SizeToContent.Height;
        this.MaxHeight = 860;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.WindowStyle = WindowStyle.SingleBorderWindow;
        this.ResizeMode = ResizeMode.NoResize;
        this.ShowInTaskbar = true;
        this.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        this.Background = new SolidColorBrush(this.isDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        this.Foreground = this.Fg();
        this.Content = this.BuildContent();

        this.Closed += (_, _) =>
        {
            this.MarkComplete();
            foreach (var cts in this.signIns.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            this.signIns.Clear();
            this.http.Dispose();
            if (ReferenceEquals(current, this))
            {
                current = null;
            }
        };
    }

    /// <summary>Shows the onboarding window, or activates the existing instance.</summary>
    public static void Show(
        IReadOnlyList<ProviderDescriptor> providers,
        ConfigStore configStore,
        UiSettingsStore uiStore,
        IUsageStore usageStore,
        Action applySettings)
    {
        if (current is { IsVisible: true })
        {
            current.Activate();
            return;
        }

        current = new OnboardingWindow(providers, configStore, uiStore, usageStore, applySettings);
        ((Window)current).Show();
        current.Activate();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(28, 26, 28, 22) };

        root.Children.Add(new TextBlock
        {
            Text = "Welcome to CodexWinBar",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Fg(),
        });
        root.Children.Add(new TextBlock
        {
            Text = "See your AI usage limits right on the taskbar. Connect the providers you use — sign-in happens "
                + "here, tokens are stored encrypted on this PC, and nothing is connected until you choose to.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.Muted(),
            Margin = new Thickness(0, 8, 0, 18),
        });

        var list = new StackPanel();
        foreach (var descriptor in this.providers)
        {
            list.Children.Add(this.BuildProviderRow(descriptor));
        }

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 560,
            Content = list,
        });

        root.Children.Add(new TextBlock
        {
            Text = "You can connect, change, or sign out of any provider later in Settings.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.Muted(),
            Margin = new Thickness(0, 16, 0, 14),
        });

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(this.MakeButton("Done", accent: true, this.Close));
        root.Children.Add(footer);

        return root;
    }

    private Border BuildProviderRow(ProviderDescriptor descriptor)
    {
        var rows = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = this.ProviderLogo(descriptor, 26);
        Grid.SetColumn(logo, 0);
        header.Children.Add(logo);

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = descriptor.Metadata.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = this.Fg(),
        });
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = this.Muted() };
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        // The editor drawer (API key / cookie / device-code) revealed under the header on demand.
        var detail = new StackPanel { Margin = new Thickness(38, 10, 0, 0), Visibility = Visibility.Collapsed };

        var action = this.BuildRowAction(descriptor, status, detail);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2);
        header.Children.Add(action);

        rows.Children.Add(header);
        rows.Children.Add(detail);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(this.isDark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(this.isDark ? Color.FromArgb(0x26, 0x00, 0x00, 0x00) : Color.FromArgb(0x19, 0x00, 0x00, 0x00)),
            Child = rows,
        };
    }

    private Button BuildRowAction(ProviderDescriptor descriptor, TextBlock status, StackPanel detail)
    {
        var oauthName = CredentialNameFor(descriptor.Id);
        var entry = this.configStore.EntryFor(this.configStore.Load(), descriptor.Id);

        // Browser OAuth (Codex, Claude, Gemini): one button runs the flow, no drawer.
        if (oauthName is not null)
        {
            var connected = this.credentialStore.Exists(oauthName);
            status.Text = connected ? "Connected" : "Sign in with your browser";
            var button = this.MakeButton(connected ? "Reconnect" : "Sign in", accent: !connected, null);
            button.Click += async (_, _) => await this.BrowserSignInAsync(descriptor, oauthName, status, button);
            return button;
        }

        // Copilot: GitHub device flow, shown in the drawer.
        if (descriptor.Id == ProviderId.Copilot)
        {
            var connected = !string.IsNullOrWhiteSpace(entry.ApiKey);
            status.Text = connected ? "Connected" : "Sign in with GitHub";
            var button = this.MakeButton(connected ? "Reconnect" : "Sign in", accent: !connected, null);
            button.Click += async (_, _) =>
            {
                detail.Visibility = Visibility.Visible;
                await this.CopilotSignInAsync(status, detail, button);
            };
            return button;
        }

        // Cursor: session cookie in the drawer.
        if (descriptor.Id == ProviderId.Cursor)
        {
            var connected = !string.IsNullOrWhiteSpace(entry.CookieHeader);
            status.Text = connected ? "Connected" : "Add your session cookie";
            this.BuildSecretEditor(
                detail,
                status,
                "Paste the WorkosCursorSessionToken cookie value (or the full Cookie header) from a signed-in cursor.com session.",
                value => this.ConnectWithEntry(descriptor.Id, e => e with { CookieHeader = NormalizeCursorCookie(value) }));
            return this.MakeDrawerToggle(connected ? "Change" : "Add cookie", detail, !connected);
        }

        // API-key providers (OpenRouter, OpenAI Admin, z.ai): key in the drawer.
        var hasKey = !string.IsNullOrWhiteSpace(entry.ApiKey);
        status.Text = hasKey ? "Connected" : "Add your API key";
        this.BuildSecretEditor(
            detail,
            status,
            $"Paste your {descriptor.Metadata.DisplayName} API key. It is stored on this PC and only sent to {descriptor.Metadata.DisplayName}.",
            value => this.ConnectWithEntry(descriptor.Id, e => e with { ApiKey = value, Source = "api" }));
        return this.MakeDrawerToggle(hasKey ? "Change" : "Add key", detail, !hasKey);
    }

    private void BuildSecretEditor(StackPanel detail, TextBlock status, string help, Action<string> onConnect)
    {
        detail.Children.Add(new TextBlock
        {
            Text = help,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.Muted(),
            Margin = new Thickness(0, 0, 0, 8),
        });
        var box = new PasswordBox
        {
            Height = 30,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(this.isDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            Foreground = this.Fg(),
            BorderBrush = new SolidColorBrush(this.isDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00)),
        };
        detail.Children.Add(box);
        var connect = this.MakeButton("Connect", accent: true, null);
        connect.Margin = new Thickness(0, 8, 0, 0);
        connect.HorizontalAlignment = HorizontalAlignment.Left;
        connect.Click += (_, _) =>
        {
            var value = box.Password?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                status.Text = "Enter a value first.";
                return;
            }

            onConnect(value);
            box.Clear();
            detail.Visibility = Visibility.Collapsed;
            status.Text = "Connected";
        };
        detail.Children.Add(connect);
    }

    private Button MakeDrawerToggle(string label, StackPanel detail, bool accent)
    {
        var button = this.MakeButton(label, accent, null);
        button.Click += (_, _) =>
            detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        return button;
    }

    private async Task BrowserSignInAsync(ProviderDescriptor descriptor, string credentialName, TextBlock status, Button action)
    {
        _ = credentialName;
        var ct = this.BeginSignIn(descriptor.Id);
        action.IsEnabled = false;
        status.Text = "Complete the sign-in in your browser...";

        try
        {
            var email = descriptor.Id switch
            {
                ProviderId.Codex => await CodexAuth.SignInAsync(
                    this.http, this.credentialStore, uri => OpenUri(uri.ToString()), ct),
                ProviderId.Claude => await ClaudeAuth.SignInAsync(
                    this.http, this.credentialStore, uri => OpenUri(uri.ToString()), ct),
                ProviderId.Gemini => await GeminiAuth.SignInAsync(
                    this.http,
                    this.credentialStore,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    uri => OpenUri(uri.ToString()),
                    ct),
                _ => throw new InvalidOperationException($"{descriptor.Metadata.DisplayName} does not support in-app sign-in."),
            };

            this.ConnectWithEntry(descriptor.Id, e => e);
            status.Text = string.IsNullOrWhiteSpace(email) ? "Connected." : $"Connected as {email}.";
            action.Content = "Reconnect";
        }
        catch (OperationCanceledException)
        {
            status.Text = "Sign-in cancelled or timed out.";
        }
        catch (Exception ex)
        {
            // Any failure (non-JSON token response, DPAPI/disk save error, no browser
            // association, etc.) is shown here; letting it escape this async void handler would
            // leave the button disabled forever, and onboarding does not reappear once closed.
            status.Text = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            action.IsEnabled = true;
        }
    }

    private async Task CopilotSignInAsync(TextBlock status, StackPanel detail, Button action)
    {
        var ct = this.BeginSignIn(ProviderId.Copilot);
        action.IsEnabled = false;
        detail.Children.Clear();
        status.Text = "Starting GitHub device flow...";

        try
        {
            var info = await CopilotAuth.StartAsync(this.http, ct);
            detail.Children.Add(new TextBlock
            {
                Text = info.UserCode,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = this.Fg(),
                Margin = new Thickness(0, 0, 0, 8),
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(this.MakeButton("Copy code", accent: false, () => Clipboard.SetText(info.UserCode)));
            buttons.Children.Add(this.MakeButton("Open GitHub", accent: false, () => OpenUri(info.VerificationUri)));
            detail.Children.Add(buttons);

            OpenUri(info.VerificationUri);
            status.Text = $"Enter the code on GitHub. Expires at {info.ExpiresAt.LocalDateTime:t}.";

            var token = await CopilotAuth.PollAsync(this.http, info, ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                status.Text = "GitHub authorization expired or was denied.";
                return;
            }

            this.ConnectWithEntry(ProviderId.Copilot, e => e with { ApiKey = token, Source = "oauth" });
            detail.Visibility = Visibility.Collapsed;
            status.Text = "Connected";
            action.Content = "Reconnect";
        }
        catch (OperationCanceledException)
        {
            status.Text = "Sign-in cancelled or timed out.";
        }
        catch (Exception ex)
        {
            // See BrowserSignInAsync: surface any failure here instead of wedging the button.
            status.Text = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            action.IsEnabled = true;
        }
    }

    // Starts a fresh 10-minute sign-in for this provider, cancelling only that provider's prior
    // attempt (the timeout stops an abandoned sign-in from holding the loopback listener open).
    private CancellationToken BeginSignIn(ProviderId id)
    {
        if (this.signIns.TryGetValue(id, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        this.signIns[id] = cts;
        return cts.Token;
    }

    // Persists the entry update, enables the provider, applies settings, and kicks a refresh.
    private void ConnectWithEntry(ProviderId id, Func<ProviderConfigEntry, ProviderConfigEntry> update)
    {
        var config = this.configStore.Load();
        var entry = this.configStore.EntryFor(config, id);
        this.configStore.Save(this.configStore.WithEntry(config, update(entry) with { Enabled = true }));
        this.applySettings();
        _ = this.usageStore.RefreshProviderAsync(id);
    }

    private void MarkComplete()
    {
        var settings = this.uiStore.Load();
        if (!settings.OnboardingCompleted)
        {
            settings.OnboardingCompleted = true;
            this.uiStore.Save(settings);
        }
    }

    private static string NormalizeCursorCookie(string raw)
    {
        var value = raw.Trim();
        return value.Contains('=', StringComparison.Ordinal) ? value : $"WorkosCursorSessionToken={value}";
    }

    private static string? CredentialNameFor(ProviderId id) => id switch
    {
        ProviderId.Codex => CodexAuth.CredentialName,
        ProviderId.Claude => ClaudeAuth.CredentialName,
        ProviderId.Gemini => GeminiAuth.CredentialName,
        _ => null,
    };

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
                Text = string.IsNullOrWhiteSpace(descriptor.Metadata.DisplayName) ? "?" : descriptor.Metadata.DisplayName.Trim()[..1].ToUpperInvariant(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private Button MakeButton(string text, bool accent, Action? onClick)
    {
        var button = new Button
        {
            Content = text,
            Height = 32,
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = this.buttonTemplate,
        };
        if (accent)
        {
            var accentColor = ReadAccentColor();
            button.Background = new SolidColorBrush(accentColor);
            button.BorderBrush = new SolidColorBrush(accentColor);
            button.Foreground = Brushes.White;
        }
        else
        {
            button.Background = new SolidColorBrush(this.isDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
            button.BorderBrush = new SolidColorBrush(this.isDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
            button.Foreground = this.Fg();
        }

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    // A minimal Button template so hover/pressed use a subtle theme-aware overlay over the button's
    // own Background instead of the default WPF system highlight (which turns bright blue and clashes
    // with white accent-button text). The button's Background/BorderBrush/Foreground still apply.
    private static ControlTemplate BuildButtonTemplate(bool dark)
    {
        // Lighten on hover in dark mode, darken in light mode; strengthen slightly when pressed.
        var hover = dark ? "#1FFFFFFF" : "#14000000";
        var pressed = dark ? "#33FFFFFF" : "#26000000";
        var xaml =
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"Button\">"
            + "<Border CornerRadius=\"4\" Background=\"{TemplateBinding Background}\" "
            + "BorderBrush=\"{TemplateBinding BorderBrush}\" BorderThickness=\"{TemplateBinding BorderThickness}\">"
            + "<Grid>"
            + $"<Border x:Name=\"overlay\" CornerRadius=\"4\" Opacity=\"0\" Background=\"{hover}\"/>"
            + "<ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" "
            + "Margin=\"{TemplateBinding Padding}\"/>"
            + "</Grid></Border>"
            + "<ControlTemplate.Triggers>"
            + "<Trigger Property=\"IsMouseOver\" Value=\"True\">"
            + "<Setter TargetName=\"overlay\" Property=\"Opacity\" Value=\"1\"/></Trigger>"
            + "<Trigger Property=\"IsPressed\" Value=\"True\">"
            + $"<Setter TargetName=\"overlay\" Property=\"Background\" Value=\"{pressed}\"/>"
            + "<Setter TargetName=\"overlay\" Property=\"Opacity\" Value=\"1\"/></Trigger>"
            + "<Trigger Property=\"IsEnabled\" Value=\"False\">"
            + "<Setter Property=\"Opacity\" Value=\"0.5\"/></Trigger>"
            + "</ControlTemplate.Triggers></ControlTemplate>";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private Brush Fg() => new SolidColorBrush(this.isDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));

    private Brush Muted() => new SolidColorBrush(this.isDark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));

    private static void OpenUri(string uri) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });

    private static bool SystemAppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : true;
    }

    private static Color ReadAccentColor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int raw)
        {
            var abgr = unchecked((uint)raw);
            return Color.FromRgb((byte)(abgr & 0xff), (byte)((abgr >> 8) & 0xff), (byte)((abgr >> 16) & 0xff));
        }

        return Color.FromRgb(0x00, 0x67, 0xC0);
    }
}
