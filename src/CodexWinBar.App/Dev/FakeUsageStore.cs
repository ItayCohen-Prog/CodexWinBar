using CodexWinBar.Core.Models;
using CodexWinBar.Core.Providers;
using CodexWinBar.Core.Scheduling;

namespace CodexWinBar.App.Dev;

/// <summary>
/// Dev-only <see cref="IUsageStore"/> that serves synthetic, provider-specific usage for every provider
/// so the UI can be exercised end-to-end without real subscriptions. Enabled by setting the environment
/// variable <c>CODEXWINBAR_FAKE=1</c>. Each provider is shaped to mirror the field mix its real parser
/// emits (percent windows vs. scalar value fields vs. credits vs. service status), which is exactly what
/// the flyout's per-field rendering and the taskbar's many-provider layout need to be tested against.
/// </summary>
internal sealed class FakeUsageStore : IUsageStore
{
    private readonly IReadOnlyList<ProviderState> states;

    public FakeUsageStore(Action<string>? log = null)
    {
        var now = DateTimeOffset.UtcNow;
        var built = Build(now);
        // CODEXWINBAR_FAKE_COUNT caps how many providers are served, to exercise the widget's
        // space-aware tiers (Full/Medium/Compact) at different provider counts.
        var limit = Environment.GetEnvironmentVariable("CODEXWINBAR_FAKE_COUNT");
        this.states = int.TryParse(limit, out var n) && n >= 0 && n < built.Count
            ? [.. built.Take(n)]
            : built;
        log?.Invoke($"FakeUsageStore active: serving {this.states.Count} synthetic providers.");
    }

    public event Action? StateChanged;

    public IReadOnlyList<ProviderState> States => this.states;

    /// <summary>True when the fake-data dev mode is requested via environment.</summary>
    public static bool IsEnabled(Func<string, string?> environment)
    {
        var value = environment("CODEXWINBAR_FAKE");
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    public void Start() => this.StateChanged?.Invoke();

    public Task RefreshAllAsync(CancellationToken ct = default)
    {
        this.StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task RefreshProviderAsync(ProviderId id, CancellationToken ct = default)
    {
        this.StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void NotifyFlyoutOpened()
    {
    }

    public void ReloadSchedule()
    {
    }

    public void Dispose()
    {
    }

    private static IReadOnlyList<ProviderState> Build(DateTimeOffset now)
    {
        return
        [
            Codex(now),
            Claude(now),
            OpenRouter(now),
            OpenAIAdmin(now),
            Copilot(now),
            Gemini(now),
            Zai(now),
            Cursor(now),
        ];
    }

    // Codex — session + weekly percent windows, a model-specific percent window, a SCALAR "reset
    // credits" field (count, not a bar), a zero credit balance, and a minor service incident.
    private static ProviderState Codex(DateTimeOffset now) => new()
    {
        Provider = ProviderId.Codex,
        ServiceStatus = new ProviderStatus
        {
            Indicator = StatusIndicator.Minor,
            Description = "Partial system degradation",
            UpdatedAt = now,
        },
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Primary = Window(18, now.AddHours(4).AddMinutes(18), 300),
            Secondary = Window(45, now.AddDays(2).AddHours(12), 10080),
            ExtraWindows =
            [
                new NamedRateWindow { Id = "gpt-5-codex", Title = "GPT-5.3-Codex", Window = Window(8, now.AddHours(4).AddMinutes(56), 10080) },
                Scalar("codex-reset-credits", "Reset credits", "3 available"),
            ],
            Credits = new CreditsSnapshot { Remaining = 0, Unit = "credits", UpdatedAt = now },
            Identity = new ProviderIdentity { Plan = "pro", AccountEmail = "reliabits@gmail.com", LoginMethod = "OAuth" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    // Claude — session + weekly, an Opus weekly tertiary, two extra percent windows, and a real
    // dollar credit balance with a limit.
    private static ProviderState Claude(DateTimeOffset now) => new()
    {
        Provider = ProviderId.Claude,
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            Primary = Window(72, now.AddHours(3).AddMinutes(23), 300),
            Secondary = Window(52, now.AddDays(5).AddHours(3), 10080),
            Tertiary = Window(20, now.AddDays(5).AddHours(3), 10080),
            ExtraWindows =
            [
                new NamedRateWindow { Id = "claude-sonnet-weekly", Title = "Sonnet weekly", Window = Window(40, now.AddDays(5).AddHours(3), 10080) },
                new NamedRateWindow { Id = "claude-routines", Title = "Routines", Window = Window(12, now.AddDays(5).AddHours(3), 10080) },
            ],
            Credits = new CreditsSnapshot { Remaining = 42.5, Limit = 50, Unit = "USD", UpdatedAt = now },
            Identity = new ProviderIdentity { Plan = "Max", LoginMethod = "OAuth" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    // OpenRouter — a single key-usage percent window plus a credit balance measured against a limit.
    private static ProviderState OpenRouter(DateTimeOffset now) => new()
    {
        Provider = ProviderId.OpenRouter,
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.OpenRouter,
            Primary = new RateWindow { UsedPercent = 62, ResetDescription = "resets daily" },
            Credits = new CreditsSnapshot { Remaining = 12.34, Limit = 20, Unit = "credits", UpdatedAt = now },
            Identity = new ProviderIdentity { AccountOrganization = "Personal", LoginMethod = "API key" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    // OpenAI Admin — no percent windows at all: a 30-day spend balance and a SCALAR "today" field.
    private static ProviderState OpenAIAdmin(DateTimeOffset now) => new()
    {
        Provider = ProviderId.OpenAIAdmin,
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.OpenAIAdmin,
            ExtraWindows = [Scalar("today", "Today", "$12.30 today")],
            Credits = new CreditsSnapshot { Remaining = 143.20, Unit = "USD (30d)", UpdatedAt = now },
            Identity = new ProviderIdentity { AccountOrganization = "acme-inc", LoginMethod = "Admin API key" },
            UpdatedAt = now,
            Confidence = DataConfidence.Estimated,
        },
    };

    // Copilot — premium-interaction primary window plus chat/completions extras, no credits.
    private static ProviderState Copilot(DateTimeOffset now) => new()
    {
        Provider = ProviderId.Copilot,
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Copilot,
            Primary = Window(22, now.AddDays(9), 43200),
            ExtraWindows =
            [
                new NamedRateWindow { Id = "chat", Title = "Chat", Window = Window(30, now.AddDays(9), 43200) },
                new NamedRateWindow { Id = "completions", Title = "Completions", Window = Window(12, now.AddDays(9), 43200) },
            ],
            Identity = new ProviderIdentity { Plan = "business", LoginMethod = "Device flow" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    // Gemini — Pro/Flash model percent windows plus per-model extras, an email identity, and a
    // MAJOR incident to exercise the red status dot.
    private static ProviderState Gemini(DateTimeOffset now) => new()
    {
        Provider = ProviderId.Gemini,
        ServiceStatus = new ProviderStatus
        {
            Indicator = StatusIndicator.Major,
            Description = "Elevated error rates on the Gemini API",
            UpdatedAt = now,
        },
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Gemini,
            Primary = Window(45, now.AddHours(9), 1440),
            Secondary = Window(25, now.AddHours(9), 1440),
            ExtraWindows =
            [
                new NamedRateWindow { Id = "gemini-2.5-pro", Title = "gemini-2.5-pro", Window = Window(45, now.AddHours(9), 1440) },
                new NamedRateWindow { Id = "gemini-2.5-flash", Title = "gemini-2.5-flash", Window = Window(25, now.AddHours(9), 1440) },
            ],
            Identity = new ProviderIdentity { AccountEmail = "dev@example.com", LoginMethod = "OAuth" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    // z.ai — token-limit + time-limit percent windows and a plan label, no credits.
    private static ProviderState Zai(DateTimeOffset now) => new()
    {
        Provider = ProviderId.Zai,
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Zai,
            Primary = Window(88, now.AddHours(3), 18000),
            Secondary = Window(44, now.AddHours(3), 300),
            Identity = new ProviderIdentity { Plan = "GLM Coding Max", LoginMethod = "API key" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    // Cursor — monthly billing model: an included-plan percent window (primary), an Auto + Composer
    // pool (secondary), a named API-models percent window, and a SCALAR on-demand "Extra usage" dollar
    // amount (a spend, not a balance — so a value row, not a bar or a credit). All three windows reset
    // at the billing-cycle end (~monthly countdown). No incident: exercises the healthy-status path.
    private static ProviderState Cursor(DateTimeOffset now) => new()
    {
        Provider = ProviderId.Cursor,
        Snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Cursor,
            Primary = Window(64, now.AddDays(23), 43200),
            Secondary = Window(38, now.AddDays(23), 43200),
            ExtraWindows =
            [
                new NamedRateWindow { Id = "cursor-api", Title = "API (models)", Window = Window(12, now.AddDays(23), 43200) },
                Scalar("cursor-extra-usage", "Extra usage", "$8.40 this cycle"),
            ],
            Identity = new ProviderIdentity { Plan = "Pro", AccountEmail = "reliabits@gmail.com", LoginMethod = "Session cookie" },
            UpdatedAt = now,
            Confidence = DataConfidence.Exact,
        },
    };

    private static RateWindow Window(double usedPercent, DateTimeOffset resetsAt, int windowMinutes) => new()
    {
        UsedPercent = usedPercent,
        WindowMinutes = windowMinutes,
        ResetsAt = resetsAt.ToUniversalTime(),
    };

    // A scalar field: a named window whose utilization is unknown, carrying only a display value.
    // The flyout renders these as a plain label -> value row rather than a percent bar.
    private static NamedRateWindow Scalar(string id, string title, string value) => new()
    {
        Id = id,
        Title = title,
        UsageKnown = false,
        Window = new RateWindow { UsedPercent = 0, ResetDescription = value },
    };
}
