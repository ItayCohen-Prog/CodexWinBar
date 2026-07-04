# CodexWinBar — Windows Architecture

> The Windows-native rebuild of [CodexBar](https://github.com/steipete/CodexBar) (macOS, Swift, MIT).
> This document is the binding contract for all implementation work. Research inputs live in
> `docs/windows-port/research/`.

## 1. Product definition (v1)

A background Windows 11 app that shows AI coding-provider usage limits **on the taskbar**, looking like a
built-in part of the OS:

- **Taskbar widget**: a compact chip near the system tray rendering per-provider usage (tiny gauge bars +
  optional percent text, e.g. `◐ 43% · W 12%`), Segoe UI Variable, theme-aware, transparent background so the
  taskbar material shows through. Two integration modes:
  - **Embedded (primary)**: `WS_CHILD` window `SetParent`-ed into `Shell_TrayWnd`, anchored left of
    `TrayNotifyWnd`. Per-pixel-alpha rendering via `UpdateLayeredWindow` (layered child windows are supported
    since Windows 8).
  - **Overlay (automatic fallback)**: top-level `WS_POPUP` + `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST`
    positioned over the same spot, tracking the taskbar rect via `SetWinEventHook`, hiding on fullscreen.
  - Runtime health checks decide: if embed fails validation (wrong ancestry, zero visibility, missing tray
    anchor), tear down and switch to overlay. `TaskbarCreated` → full re-embed.
- **Flyout**: WPF borderless popup above the widget — provider cards (header, usage bars, reset countdowns,
  credits, status incidents, errors), Refresh / Settings / Quit actions. Acrylic backdrop
  (`DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`), `DWMWCP_ROUND` corners, dark-mode aware,
  light-dismiss.
- **Settings window**: WPF, panes General / Display / Providers / About.
- **Tray icon**: always present (`Shell_NotifyIcon`) — opens the same flyout; context menu with
  Refresh / Settings / Widget toggle / Quit; balloon (`NIF_INFO`) quota notifications (render as toasts on Win11).
- **Providers v1** (7): Codex, Claude, OpenRouter, OpenAI Admin, Copilot, Gemini, z.ai.
- Launch at login (HKCU Run key), single instance (named mutex + named pipe activation).

Non-goals for v1: browser-cookie import, WebView2 probes, CLI PTY probes, multi-account token switching,
cost/token local log scanning, Velopack auto-update (planned v1.1), MSIX.

## 2. Stack

- **C# / .NET 9** (SDK 9.0.202 pinned via `global.json`), `net9.0-windows`.
- **WPF** for flyout + settings (lazy-created). **No UI framework packages** — hand-rolled Fluent-ish styles.
- **Win32 interop** (P/Invoke, no CsWin32 codegen dependency) for widget, DWM, tray, hooks, theme.
- **Zero external NuGet dependencies.** `System.Text.Json` (source-generated contexts), `HttpClient`.
  This keeps footprint low and lets sandboxed tooling build with `--no-restore`.
- Publishing: self-contained single-file win-x64, ReadyToRun. GitHub Actions CI (`windows-latest`).

## 3. Solution layout

```
CodexWinBar.sln
global.json
Directory.Build.props            (LangVersion latest, nullable enable, TFM net9.0-windows, warnings as errors)
src/
  CodexWinBar.Core/              netstandard-agnostic engine (class lib, net9.0 — NO WPF/WinForms deps)
    Models/                      UsageSnapshot, RateWindow, NamedRateWindow, CreditsSnapshot,
                                 ProviderIdentity, ProviderStatus, DataConfidence, FetchOutcome
    Providers/                   ProviderId (enum), ProviderDescriptor, ProviderMetadata, ProviderBranding,
                                 IFetchStrategy, FetchContext, FetchPipeline, ProviderRegistry
    Config/                      CodexBarConfig (upstream-compatible), ConfigStore (resolution+atomic write),
                                 UiSettings (+ store, %APPDATA%\CodexWinBar\ui-settings.json)
    Scheduling/                  RefreshScheduler (interval + reset-boundary + startup retry), UsageStore
    Status/                      StatusPoller (Statuspage.io + Google Workspace incidents)
    Http/                        ProviderHttpClient (shared handler, timeouts, UA)
    Auth/                        OAuthTokenRefresher helpers, CredentialFile readers
    Json/                        CoreJsonContext (source-generated)
  CodexWinBar.Providers/         class lib; one folder per provider, each exposing a ProviderDescriptor
    Codex/  Claude/  OpenRouter/ OpenAIAdmin/  Copilot/  Gemini/  Zai/
  CodexWinBar.Widget/            class lib; Win32 only, NO WPF
    NativeMethods.cs             all P/Invoke in one place
    TaskbarInterop.cs            find taskbar/tray, rects, appbar state, alignment
    ThemeReader.cs               AppsUseLightTheme/SystemUsesLightTheme/EnableTransparency/accent, change events
    WidgetWindow.cs              HWND lifecycle, embed/overlay state machine, TaskbarCreated handling
    WidgetRenderer.cs            GDI+ → premultiplied ARGB DIB → UpdateLayeredWindow; DPI-aware layout
    FullscreenDetector.cs        foreground-window monitor coverage checks (overlay mode)
  CodexWinBar.App/               WPF exe (OutputType WinExe), entry point
    Program.cs / App.xaml        single instance, composition root, DI-free wiring
    Tray/TrayIcon.cs             Shell_NotifyIcon wrapper + balloons + context menu
    Flyout/FlyoutWindow.xaml     provider cards UI, positioning, light dismiss
    Settings/SettingsWindow.xaml panes
    Interop/WpfDwm.cs            backdrop/corner/dark-mode attributes for WPF HWNDs
    Notifications/QuotaNotifier.cs  thresholds [50,20], depleted/restored, balloon dispatch
    Startup/StartupManager.cs    HKCU Run key
tests/
  CodexWinBar.Core.Tests/        xunit (test-only NuGet deps — restored in dev/CI, never shipped; see §10)
  CodexWinBar.Providers.Tests/   golden-file parser tests per provider
```

## 4. Core contracts (binding)

```csharp
public enum ProviderId { Codex, Claude, OpenRouter, OpenAIAdmin, Copilot, Gemini, Zai }

public sealed record RateWindow {
    public required double UsedPercent { get; init; }        // 0..100
    public int? WindowMinutes { get; init; }                  // 300 = 5h, 10080 = weekly
    public DateTimeOffset? ResetsAt { get; init; }
    public string? ResetDescription { get; init; }
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
}

public sealed record UsageSnapshot {
    public required ProviderId Provider { get; init; }
    public RateWindow? Primary { get; init; }                 // session (5h)
    public RateWindow? Secondary { get; init; }               // weekly
    public RateWindow? Tertiary { get; init; }                // model-specific weekly (e.g. Opus)
    public IReadOnlyList<NamedRateWindow> ExtraWindows { get; init; } = [];
    public CreditsSnapshot? Credits { get; init; }
    public ProviderIdentity? Identity { get; init; }          // email / org / plan / login method
    public DateTimeOffset UpdatedAt { get; init; }
    public DataConfidence Confidence { get; init; }           // Exact | Estimated | PercentOnly | Unknown
}

public interface IFetchStrategy {
    string Id { get; }                                        // e.g. "oauth", "api-key"
    FetchKind Kind { get; }                                   // Oauth | ApiToken | LocalProbe | Web | Cli
    bool IsAvailable(FetchContext ctx);
    Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct);
    bool ShouldFallback(Exception error, FetchContext ctx);
}

public sealed class ProviderDescriptor {
    public required ProviderId Id { get; init; }
    public required ProviderMetadata Metadata { get; init; }  // displayName, sessionLabel, weeklyLabel,
                                                              // defaultEnabled, dashboardURL, statusPageURL, …
    public required ProviderBranding Branding { get; init; }  // glyph key + Color
    public required IReadOnlyList<IFetchStrategy> Strategies { get; init; }  // ordered; pipeline = upstream semantics
}
```

Pipeline semantics (port of upstream `ProviderFetchPipeline`): iterate strategies in order, skip unavailable,
stop on first success; on error continue only if `ShouldFallback`; record every attempt
(`strategyId`, `kind`, `wasAvailable`, `error`) into a `FetchOutcome` for diagnostics; throw
`NoAvailableStrategyException` if nothing ran.

## 5. Config (upstream-compatible)

Resolution order (identical to upstream): `CODEXBAR_CONFIG` env override → absolute `XDG_CONFIG_HOME`/codexbar/config.json
→ existing `%USERPROFILE%\.config\codexbar\config.json` → existing legacy `%USERPROFILE%\.codexbar\config.json`
→ new default `%USERPROFILE%\.config\codexbar\config.json`. Schema `version: 1`, `providers[]` entries with
`id`, `enabled`, `source` (`auto|web|cli|oauth|api`), `apiKey`, `cookieHeader`, `cookieSource`, `region`,
`workspaceID`, `enterpriseHost`, `quotaWarnings { session|weekly: { thresholds[], enabled } }` (defaults `[50,20]`).
Unknown fields must round-trip (use `JsonExtensionData` or plain `JsonNode` doc editing) — a Windows write must
not destroy fields the macOS app wrote. Atomic write: temp file + `File.Replace`; ACL the file to the current
user only. Unknown provider ids in the file are preserved but not surfaced.

UI prefs (not in config.json, mirrors upstream UserDefaults): `%APPDATA%\CodexWinBar\ui-settings.json` —
refreshCadence (`manual|1|2|5|15|30` min, default 5), mergeIcons (default true), displayTextMode
(`percent|pace|both|resetTime`, default percent), usageBarsShowUsed (default false), resetTimesShowAbsolute
(default false), launchAtLogin (default false), statusChecksEnabled (default true), notifications toggles,
widgetMode (`auto|embedded|overlay|hidden`, default auto), widgetSide (`right|left`, default right).

## 6. Provider protocols (v1)

Implementers MUST read the corresponding research spec before coding; constants below are the contract:

| Provider | Auth source | Usage fetch |
|---|---|---|
| **Codex** | `%CODEX_HOME%\auth.json` else `%USERPROFILE%\.codex\auth.json`: `{OPENAI_API_KEY?, tokens{id_token,access_token,refresh_token,account_id}, last_refresh}`; refresh `POST https://auth.openai.com/oauth/token`, client `app_EMoamEEZ73f0CkXaXp7hrann`, grant `refresh_token`, scope `openid profile email` | `GET https://chatgpt.com/backend-api/wham/usage`, headers `Authorization: Bearer`, `User-Agent: CodexWinBar`, `Accept: application/json`, optional `ChatGPT-Account-Id`; windows mapped by duration: 300m→session, 10080m→weekly; reset credits `GET …/wham/rate-limit-reset-credits` w/ `OpenAI-Beta: codex-1`. Details: `research/codex-provider.md` |
| **Claude** | `%USERPROFILE%\.claude\.credentials.json`: `{claudeAiOauth{accessToken,refreshToken,expiresAt,scopes,subscriptionType,rateLimitTier}}`; requires `user:profile` scope; refresh constants extracted from upstream `Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/*` at implementation time | `GET https://api.anthropic.com/api/oauth/usage`, headers `Authorization: Bearer`, `anthropic-beta: oauth-2025-04-20`; map `five_hour`→session, `seven_day`→weekly, `seven_day_opus`→tertiary; plan from `subscriptionType`/`rate_limit_tier`. Details: upstream `docs/CLAUDE.md` |
| **OpenRouter** | config `apiKey` or `OPENROUTER_API_KEY` | `GET https://openrouter.ai/api/v1/credits`, `GET https://openrouter.ai/api/v1/key` |
| **OpenAI Admin** | `OPENAI_ADMIN_KEY` / config key | `GET https://api.openai.com/v1/organization/costs`, `…/usage/completions` |
| **Copilot** | GitHub device flow (`https://github.com/login/device/code`, `…/oauth/access_token`), token in config | `GET https://api.github.com/copilot_internal/user`, `Authorization: token <tok>` |
| **Gemini** | `%USERPROFILE%\.gemini\oauth_creds.json`; refresh `POST https://oauth2.googleapis.com/token` | `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota` |
| **z.ai** | config `apiKey` / `Z_AI_API_KEY` | `GET https://api.z.ai/api/monitor/usage/quota/limit` |

Every provider: 15s HTTP timeout, no retries inside a fetch (scheduler handles cadence), map HTTP 401/403 →
`UnauthorizedProviderException` (UI shows "re-authenticate" guidance), parse defensively (missing windows OK).

## 7. Refresh engine (ported constants)

- Cadence: manual/60/120/300/900/1800s, default 300.
- One batch at a time (`isRefreshing` gate); per-provider coalescing (a second request while in-flight waits
  for/replaces the current one).
- **Reset-boundary refresh**: after each batch scan all windows' `ResetsAt`; schedule one-shot refresh at
  `resetsAt + 30s` (min `now + 5s`) if earlier than next normal tick and boundary not already attempted;
  remember ≤ 64 boundaries.
- **Flyout-open refresh**: 1.2s after flyout opens, refresh providers that are stale (last fetch errored) or
  never fetched.
- Startup connectivity retry with growing delays until first success.
- Staleness = last fetch for that provider errored (no TTL).
- Status polling (if enabled): Statuspage `GET <statusPage>/api/v2/status.json` (10s timeout), Google Workspace
  incidents feed for Gemini; keep previous status on poll error; indicator enum
  `None|Minor|Major|Critical|Maintenance|Unknown`.

## 8. Widget spec

- Class `CodexWinBarWidget`. Process is per-monitor-v2 DPI aware (app manifest).
- Layout (per enabled provider, merged single chip): `[glyph 12px] [bar-pair 16×10px] [text]`, 12–13px Segoe UI
  Variable Text, vertical center in taskbar; chip padding 8px; provider separator 10px. Bars: 2 stacked
  rounded-rect gauges (session top, weekly bottom), track = fg @ 25% alpha, fill = provider brand color
  (fallback: theme fg); remaining-percent fill by default (`usageBarsShowUsed` flips).
- Text color: white in dark taskbar, `#1B1B1B` in light (from `SystemUsesLightTheme`); 90% opacity.
- Render loop: only on data/theme/DPI change — no timers except a 1-minute tick to refresh countdown text when
  `displayTextMode == resetTime`.
- Interactions: hover = subtle background pill (fg @ 8%); left-click = toggle flyout anchored to chip;
  right-click = tray context menu equivalents. Tooltip = combined summary.
- Embed mode: after `SetParent`, re-assert position on `EVENT_OBJECT_LOCATIONCHANGE` of tray + on
  `WM_SETTINGCHANGE`/`WM_DISPLAYCHANGE`/`WM_DPICHANGED` (debounced 250ms). Health check after each reposition;
  two consecutive failures → overlay mode (log + tray balloon once).
- Explorer restart: `TaskbarCreated` registered message → destroy + re-create + re-embed (debounced).
- Overlay mode extras: hide when taskbar auto-hides or fullscreen foreground on same monitor.
- Primary monitor only in v1.

## 9. Flyout & settings spec

- Flyout: WPF `Window`, `WindowStyle=None`, `AllowsTransparency=False` (needed for DWM backdrop),
  `ShowInTaskbar=False`, `Topmost=True`; width 348px; acrylic + round corners + dark mode via DWM attributes;
  position above widget chip clamped to work area; light-dismiss on `Deactivated` + Esc.
- Card per enabled provider: header (glyph, name, plan/email subtitle or error subtitle), usage rows
  (label, 6px bar, `XX%` + `resets in 2h 13m`), credits row when present, status incident row when non-None.
  Footer: Refresh (spinner state), Settings, Quit. All text selectable-free, keyboard focusable.
- Settings: General (launch at login, refresh cadence combo, status checks, notifications), Display
  (widget mode/side, display text mode, show-used, absolute reset times), Providers (list with enable toggle,
  source combo where applicable, API-key `PasswordBox` writing to config, per-provider auth state + "Sign in"
  for Copilot device flow), About (version, upstream attribution, licenses).

## 10. Testing & CI

- `tests/CodexWinBar.Core.Tests`: config resolution/round-trip, pipeline fallback semantics, scheduler
  boundary math (fake clock), snapshot JSON round-trip.
- `tests/CodexWinBar.Providers.Tests`: golden JSON fixtures per provider → parsed snapshot assertions;
  auth-file readers with fixture files; fake `HttpMessageHandler`.
- xunit + `Microsoft.NET.Test.Sdk` are the only (test-only) NuGet deps; app projects stay dependency-free.
- CI (`.github/workflows/ci.yml`): windows-latest; `dotnet build -c Release`; `dotnet test -c Release`;
  `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true`;
  upload artifact.

## 11. Work breakdown (agent waves, disjoint file ownership)

- **W0 (orchestrator)**: solution, csproj, `Directory.Build.props`, all §4 contract types, empty descriptors,
  stub windows — compiles green.
- **WA1** Core/Config + Json; **WA2** Core/Scheduling + Status + Http + UsageStore; **WA3** Widget project
  (interop + window + renderer).
- **WB1** Codex provider; **WB2** Claude provider; **WB3** OpenRouter + Zai + OpenAIAdmin; **WB4** Copilot +
  Gemini.
- **WC1** App shell + tray + startup + single-instance; **WC2** Flyout; **WC3** Settings + notifier.
- **WD** tests wave + integration fix wave (orchestrator drives builds; fixer agents get compiler output).

Rules for all implementation agents: own ONLY your listed paths; never edit contracts in §4 (request changes
via your report instead); no new NuGet references; C# 13, nullable enabled, file-scoped namespaces; every
public type XML-doc'd; match upstream behavior constants exactly.

---

## 12. Amendments — adopted from adversarial review (binding, overrides earlier sections)

### Engine
- **A1 Wire ids**: config parsing/writing uses lowercase upstream ids via `ProviderIds.ConfigId()` /
  `TryParse()` (`OpenAIAdmin` ↔ `"openai"`). Enum names never appear in files.
- **A2 RateWindow** gains `double? NextRegenPercent` and `bool IsSyntheticPlaceholder`.
- **A3 CreditsSnapshot** stays simplified (`Remaining`, `Limit?`, `Unit`, `UpdatedAt`) — intentional v1
  divergence from upstream credit events; Codex reset-credits surface later via `ExtraWindows` if needed.
- **A4 Config fidelity**: `ProviderConfigEntry` models ALL known upstream fields (`extrasEnabled`, `secretKey`,
  `tokenAccounts`, `codexActiveSource`, `codexProfileHomePaths`, `kiloKnownOrganizations`,
  `kiloEnabledOrganizationIDs`, `awsProfile`, `awsAuthMode` — complex ones as raw `JsonElement`) plus
  `[JsonExtensionData]` at root and entry level. Saves preserve entry ORDER and unknown entries verbatim;
  edits patch the loaded model, never rebuild from defaults. Root object also carries extension data.
- **A5 quotaWarnings normalization**: thresholds clamped 0–99, deduplicated, sorted descending; empty → `[50,20]`.
- **A6 XDG**: `XDG_CONFIG_HOME` honored only when `Path.IsPathFullyQualified`; relative values ignored;
  default remains `%USERPROFILE%\.config\codexbar\config.json` (upstream-compatible, no migration).
- **A7 Codex protocol fidelity**: usage timeout **30s**, reset-credits timeout **4s**; `User-Agent: CodexBar`
  (exact upstream string for server compatibility); usage sends `ChatGPT-Account-Id`, reset-credits sends
  `ChatGPT-Account-ID` + `OpenAI-Beta: codex-1` + `originator: Codex Desktop`. Other providers keep the 15s default.
- **A8 Coalescing state machine** (per provider): `generation` counter + single in-flight task. A new request
  while in-flight cancels the old task and increments the generation; a completed fetch publishes only if its
  generation is still current; cancellation without a newer published snapshot marks the provider
  retry-required (stale). Batch refresh keeps the global `isRefreshing` gate.
- **A9 Reset boundaries**: all reset instants normalized to UTC on parse (`DateTimeOffset.FromUnixTimeSeconds`
  for epoch payloads); attempted-boundary key = `(providerId, windowSlot, unixSeconds)`.
- **A10 JSON**: every serialized DTO registered in a source-generated context; extension-data members included;
  round-trip covered by golden-fixture tests (unknown providers/fields, tokenAccounts, casing).
- **A11 Copilot device-flow UX**: Settings shows `user_code` + copies it, opens `verification_uri`, polls at the
  server-provided interval with cancel/expiry/denied surfaced inline; token stored in config `apiKey` field
  (upstream-compatible).
- **A12 Ownership**: orchestrator owns `ProviderRegistry`, `CoreJsonContext`, and all §4 contract files;
  provider agents ship self-contained folders (descriptor factory + local DTOs/JsonContext) and request
  registry/context registration lines via their reports.

### Widget / UI
- **A13 Mode policy**: `auto` = attempt embedded, validate via capability probe, fall back to overlay. The probe
  (embedded): `SetParent` succeeded; ancestor chain reaches current `Shell_TrayWnd`; owner process is Explorer;
  style is `WS_CHILD` (no `WS_POPUP`); rect non-empty, on taskbar monitor, intersects taskbar client rect,
  adjacent to `TrayNotifyWnd`; `UpdateLayeredWindow` (per-pixel alpha child) succeeds. Failure counting is
  SUSPENDED while: taskbar auto-hidden, Explorer restarting, display topology changing, fullscreen suppression.
  Two consecutive validated failures → overlay for the session (+ log, one-time balloon).
- **A14 SetParent transition** (exact): create with `WS_POPUP` hidden → `SetParent(widget, trayWnd)` → clear
  `WS_POPUP|WS_CAPTION|WS_THICKFRAME|WS_SYSMENU`, set `WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN`;
  ex-style: clear `WS_EX_APPWINDOW`, keep `WS_EX_TOOLWINDOW|WS_EX_LAYERED`; then
  `SetWindowPos(SWP_FRAMECHANGED|SWP_NOACTIVATE|SWP_SHOWWINDOW)`.
- **A15 Threading**: dedicated `WidgetHost` STA thread owns ALL widget/controller HWNDs, the message pump,
  `SetWinEventHook` hooks, and `TaskbarCreated` handling. WPF → widget: immutable render-state snapshots
  posted via custom message; widget → WPF (clicks): `Dispatcher.BeginInvoke`.
- **A16 Flyout backdrop on WPF**: `AllowsTransparency=False`; after `SourceInitialized` set
  `DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_TRANSIENTWINDOW`, `DWMWA_USE_IMMERSIVE_DARK_MODE`, `DWMWCP_ROUND`; set
  `Window.Background=Transparent` AND `HwndSource.CompositionTarget.BackgroundColor=Transparent` so the
  backdrop shows through; verify both themes.
- **A17 Light dismiss**: `Deactivated` + `WH_MOUSE_LL` outside-click (installed only while open) + `Esc` +
  ignore the opening click; also close on `TaskbarCreated`/display changes.
- **A18 Flyout DPI**: compute placement in physical pixels, convert via the target monitor's
  `CompositionTarget.TransformFromDevice` before assigning `Left/Top`; handle `WM_DPICHANGED`.
- **A19 Notifications v1**: tray balloons (`NIF_INFO` + `NIIF_RESPECT_QUIET_TIME`, deduped per
  provider+window+threshold per reset period) — documented as best-effort; real toasts (AUMID + COM activator)
  deferred to v1.1.
- **A20 Constraints**: RTL shell (`WS_EX_LAYOUTRTL` on `Shell_TrayWnd`) → force overlay mode. `widgetSide:left`
  → overlay mode only. Bottom taskbar edge only; other `ABM_GETTASKBARPOS` edges → overlay with edge-aware
  placement or hidden with logged reason. While tray overflow (`NotifyIconOverflowWindow`) is visible, overlay
  hides; embedded revalidates z-order after it closes. Primary monitor only.
