# Core architecture spec

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Key facts

- Providers are compile-time `UsageProvider` enum cases registered exhaustively in `ProviderDescriptorRegistry`; missing descriptors precondition-fail at bootstrap (Sources/CodexBarCore/Providers/ProviderDescriptor.swift:47-120).
- A provider is descriptor metadata + branding + token-cost config + fetch plan + CLI config; concrete fetch behavior lives in ordered `ProviderFetchStrategy` pipelines (Sources/CodexBarCore/Providers/ProviderDescriptor.swift:13-44, ProviderFetchPlan.swift:176-264).
- Config resolves in order: `CODEXBAR_CONFIG` override, then absolute `$XDG_CONFIG_HOME/codexbar/config.json`, then existing `~/.config/codexbar/config.json`, then existing legacy `~/.codexbar/config.json`, else new XDG default (docs/configuration.md:14-21, Sources/CodexBarCore/Config/CodexBarConfigStore.swift:76-116).
- Config file version is `1`; writes are atomic, pretty-printed, sorted-key JSON with 0600 permissions on macOS/Linux (Sources/CodexBarCore/Config/CodexBarConfigStore.swift:53-124).
- Refresh frequency is NOT in config.json — it's UserDefaults-backed (SettingsStore), with options manual/1m/2m/5m/15m/30m (60/120/300/900/1800 seconds); 5m is default (Sources/CodexBar/SettingsStore.swift:6-27).
- Reset-boundary refresh: schedules a refresh at resetsAt + 30s grace, minimum 5s delay, only if before next normal refresh and not already attempted; caps at 64 remembered boundaries (Sources/CodexBar/UsageStore.swift:107-111, UsageStore+ResetBoundaryRefresh.swift:10-129).
- Menu-open refresh delay is 1.2s (defaultMenuOpenRefreshDelay); refreshes only stale/missing visible providers by default, or all enabled providers if `refreshAllProvidersOnMenuOpen` UserDefaults key is set (Sources/CodexBar/StatusItemController+Menu.swift:10-16, 1104-1173).
- Adaptive/predictive refresh policy is DESIGNED but NOT IMPLEMENTED (docs/predictive-refresh-policy.md:9-23); the approved design uses menu-open recency and thermal/power state to pick 2m/5m/15m/30m intervals.
- Staleness is purely error-based: `isStale(provider)` = `errors[provider] != nil` (Sources/CodexBar/UsageStore.swift:494-508) — no TTL-based staleness was found.
- Status polling: Statuspage.io providers (OpenAI, Claude, Cursor, Factory, Copilot) hit `<host>/api/v2/status.json` (10s timeout) plus incident.io grouped feed and summary/components fallbacks; Google Workspace providers (Gemini, Antigravity) hit `https://www.google.com/appsstatus/dashboard/incidents.json` (10s timeout). Previous status is kept on error to avoid flapping (Sources/CodexBar/UsageStore+Status.swift, UsageStore.swift:911-945).
- No wake-notification (`NSWorkspace.didWakeNotification`) or network-change (`NWPathMonitor`) immediate-refresh trigger was found in the inspected files — only startup connectivity retry and menu-open refresh are confirmed triggers.

## Windows portability

Clean ports: the descriptor/strategy architecture, enum provider IDs, config JSON shape, source modes (auto/web/cli/oauth/api), fetch-outcome attempt tracking, normalized `UsageSnapshot`/`CreditsSnapshot` data model, status indicator model, and reset-boundary refresh logic all port conceptually without change — these are just data structures and scheduling logic, not macOS-specific.

Config paths: preserve `CODEXBAR_CONFIG` env var override for compatibility. On Windows, prefer `%APPDATA%\CodexWinBar\config.json` as the native default while optionally reading `%USERPROFILE%\.config\codexbar\config.json` and `%USERPROFILE%\.codexbar\config.json` for one-time migration/import from a macOS-synced dotfile setup. Windows has no POSIX `0600`; use a user-only ACL on the file/directory (icacls granting only the current SID, removing inherited broad access) as the closest equivalent.

Secrets: replace macOS Keychain with Windows Credential Manager (via `CredWrite`/`CredRead` or a wrapper) or DPAPI (`CryptProtectData`) for locally-encrypted secrets. Avoid storing raw cookies/API tokens in the registry or unprotected JSON unless the user explicitly opts into file-backed secrets (matching CodexBar's own `apiKey`/`cookieHeader` config fields).

Browser cookie import: macOS Safari/Keychain-based cookie sourcing (`Sources/CodexBarCore/Providers/Providers.swift:201-278`, e.g. Codex prefers Safari first, Grok/Devin/Copilot/Qoder are Chrome-only, Cursor prefers Safari first) does not port. Windows equivalents: Chrome/Edge/Firefox profile discovery under `%LOCALAPPDATA%\Google\Chrome\User Data` / `%LOCALAPPDATA%\Microsoft\Edge\User Data` / `%APPDATA%\Mozilla\Firefox\Profiles`, with DPAPI-backed decryption for Chromium's encrypted cookie store (`CryptUnprotectData`, or AES-GCM with the OS-protected key for Chrome v80+). Safari-specific default ordering should be dropped or remapped to Edge/Chrome-first on Windows.

Taskbar integration ("looks like a built-in part of the OS"): replace macOS `NSStatusItem`/AppKit menu tracking with a Windows Shell_TrayWnd-adjacent approach — a notification-area icon (`Shell_NotifyIcon`) is the closest analog to a menu-bar item, though true taskbar embedding (not just tray) would require Explorer shell extension APIs or a custom always-on-top window docked to the taskbar. For icon persistence across Explorer restarts, handle the registered `WM_TASKBARCREATED` message to re-add the notification icon. For sleep/resume triggers (if added, since CodexBar itself has none confirmed), use `WM_POWERBROADCAST` with `PBT_APMRESUMEAUTOMATIC`/`PBT_APMRESUMESUSPEND`. For network-change triggers (also not confirmed in CodexBar), use `NetworkInformation.NetworkStatusChanged` (WinRT) or `NotifyIpInterfaceChange`/`NotifyAddrChange` (Win32).

Scheduling: use a single app-owned timer (e.g., a `System.Threading.Timer`/thread-pool timer or async loop) rather than Windows Task Scheduler, matching CodexBar's in-process fixed-interval timer with cancel/reschedule semantics. Preserve the "one batch refresh at a time, per-provider coalescing" model and reset-boundary scheduling logic verbatim (30s grace + 5s minimum delay constants can be copied directly since they're not OS-specific).

Status/UI: `ProviderStatusIndicator` (none/minor/major/critical/maintenance/unknown) maps cleanly to a Windows overlay-icon badge system (`ITaskbarList3::SetOverlayIcon` for taskbar button overlays, or tray icon swap). Keep the "preserve previous status on polling error to avoid flapping" behavior.

CLI/version detection: descriptor-provided CLI names/aliases/version-detector callbacks port conceptually, but process discovery must account for Windows PATH/PATHEXT semantics (`.exe`/`.cmd`/`.ps1` resolution) and use `CreateProcess`/`System.Diagnostics.Process` with proper timeout handling instead of POSIX `fork`/`exec`.

## Open questions

- The task asked about jitter/backoff in the refresh loop; inspected files show fixed timer sleeps, reset-boundary scheduling, and startup connectivity retry, but no adaptive scheduler-level backoff or jitter constant was found in the scoped slice — this may exist in provider-specific fetch strategies outside the read files.
- The task asked what triggers immediate refresh on wake/network change; no `NSWorkspace.didWakeNotification` or `NWPathMonitor` trigger was found in the inspected files. Only startup connectivity retry and menu-open refresh (1.2s delay) were confirmed as immediate-refresh triggers.
- Full provider endpoint URLs live in individual provider descriptor files (one per provider under Sources/CodexBarCore/Providers/), but the execution constraint (~15 helper files max) limited Codex to the explicitly named files plus directly-referenced helpers, so not every provider descriptor's exact API endpoint was extracted.
- The full UI preference schema is UserDefaults-backed across multiple `SettingsStore+*.swift` files; only the refresh-frequency, status-checks-enabled, and menu-open-refresh-all keys directly relevant to the requested core loop were inspected — a complete list of all UserDefaults keys was not compiled.
- Provider-specific nested usage payload schemas (e.g., Kiro/Amp/OpenRouter/OpenAI-API detail structures embedded in UsageSnapshot) were not expanded in this pass; the Windows port can replicate the normalized envelope first and add provider-specific payload shapes provider-by-provider in a follow-up pass.
- Codex's first attempt failed under `-s read-only` sandbox mode specifically (`CreateProcessWithLogonW failed: 5`) — this appears to be an environment/Windows-sandbox-policy issue on this machine, not a task issue; future read-only-only tasks in this environment may need to default to workspace-write or a different sandbox policy to succeed on the first try.

## Full report

# CodexBar Core Architecture Specification

## 1. Provider abstraction

CodexBar splits provider behavior into descriptor metadata plus fetch strategies. The core module owns descriptors, fetch strategies, probes, parsing, and shared utilities; the app module owns UI/state and provider settings/login/menu hooks (`docs/architecture.md:10-24`, `docs/provider.md:26-43`). Provider IDs are compile-time `UsageProvider` enum cases and are used for persistence and widgets (`docs/provider.md:26-33`, `Sources/CodexBarCore/Providers/Providers.swift:5-62`).

A `ProviderDescriptor` contains exactly: `id: UsageProvider`, `metadata: ProviderMetadata`, `branding: ProviderBranding`, `tokenCost: ProviderTokenCostConfig`, `fetchPlan: ProviderFetchPlan`, and `cli: ProviderCLIConfig` (`Sources/CodexBarCore/Providers/ProviderDescriptor.swift:13-35`). It exposes `fetchOutcome(context:)` and `fetch(context:)` by delegating to the fetch plan (`Sources/CodexBarCore/Providers/ProviderDescriptor.swift:37-44`).

`ProviderMetadata` fields are: `id`, `displayName`, `sessionLabel`, `weeklyLabel`, `opusLabel`, `supportsOpus`, `supportsCredits`, `creditsHint`, `toggleTitle`, `cliName`, `defaultEnabled`, `isPrimaryProvider`, `usesAccountFallback`, `browserCookieOrder`, `dashboardURL`, `subscriptionDashboardURL`, `changelogURL`, `statusPageURL`, `statusLinkURL`, and `statusWorkspaceProductID` (`Sources/CodexBarCore/Providers/Providers.swift:124-192`). The provider authoring guide describes these as labels/URLs/defaults/capabilities/fetch pipeline/CLI metadata/account behavior (`docs/provider.md:45-55`).

`ProviderBranding` is `{ iconStyle: IconStyle, iconResourceName: String, color: ProviderColor }`; `ProviderColor` is `{ red: Double, green: Double, blue: Double }` (`Sources/CodexBarCore/Providers/ProviderBranding.swift:3-25`). `ProviderCLIConfig` is `{ name: String, aliases: [String], versionDetector: ((BrowserDetection) -> String?)? }` (`Sources/CodexBarCore/Providers/ProviderCLIConfig.swift:3-17`). Token-cost capability is `ProviderTokenCostConfig { supportsTokenCost: Bool, noDataMessage: () -> String }` (`Sources/CodexBarCore/Providers/ProviderDescriptor.swift:3-10`).

Fetch source modes are exactly `auto`, `web`, `cli`, `oauth`, `api`; `usesWeb` is true for `auto` and `web` (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:8-18`). Fetch kinds are exactly `cli`, `web`, `oauth`, `apiToken`, `localProbe`, and `webDashboard` (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:167-174`). A `ProviderFetchStrategy` must provide `id`, `kind`, `isAvailable(context:)`, `fetch(context:)`, and `shouldFallback(on:context:)` (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:176-182`). A strategy returns `ProviderFetchResult { usage, credits, dashboard, sourceLabel, strategyID, strategyKind, claudeOAuthKeychainPersistentRefHash, claudeOAuthHistoryOwnerIdentifier }` (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:97-129`).

The fetch context includes runtime, source mode, credit/optional usage flags, web timeout/debug flags, verbosity, environment, settings snapshot, core fetchers, browser detection, selected token account, token updaters, cost-history days clamped to 1...365, and persistent CLI session controls (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:20-89`). A `ProviderFetchPipeline` resolves ordered strategies, records attempts, skips unavailable strategies, stops on non-fallback errors, falls back when `shouldFallback` returns true, and returns `noAvailableStrategy(provider)` if none succeed (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:201-264`). Each attempt records `strategyID`, `kind`, `wasAvailable`, and optional `errorDescription` (`Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:132-153`).

Registration is exhaustive and descriptor-driven. `ProviderDescriptorRegistry.descriptorsByID` maps every `UsageProvider` case to a descriptor and `bootstrap` preconditions if any enum case lacks a descriptor (`Sources/CodexBarCore/Providers/ProviderDescriptor.swift:47-120`). Runtime lookup uses `descriptor(for:)`; CLI name/alias lookup is built from every descriptor's CLI config (`Sources/CodexBarCore/Providers/ProviderDescriptor.swift:137-165`). Current provider IDs are: `codex`, `openai`, `azureopenai`, `claude`, `cursor`, `opencode`, `opencodego`, `alibaba`, `alibabatokenplan`, `factory`, `gemini`, `antigravity`, `copilot`, `devin`, `zai`, `minimax`, `manus`, `kimi`, `kilo`, `kiro`, `vertexai`, `augment`, `jetbrains`, `kimik2`, `moonshot`, `amp`, `t3chat`, `ollama`, `synthetic`, `warp`, `openrouter`, `elevenlabs`, `windsurf`, `zed`, `perplexity`, `mimo`, `doubao`, `sakana`, `abacus`, `mistral`, `deepseek`, `codebuff`, `crof`, `venice`, `commandcode`, `qoder`, `stepfun`, `bedrock`, `grok`, `groq`, `llmproxy`, `litellm`, `deepgram`, `poe`, `chutes`, `crossmodel` (`Sources/CodexBarCore/Providers/Providers.swift:5-62`, `docs/configuration.md:170-172`).

Provider enablement comes from config entries. `CodexBarConfig.makeDefault()` creates one `ProviderConfig` per `UsageProvider` with `enabled` set to descriptor metadata `defaultEnabled`; `normalized()` deduplicates entries and appends omitted providers with defaults (`Sources/CodexBarCore/Config/CodexBarConfig.swift:14-23`, `Sources/CodexBarCore/Config/CodexBarConfig.swift:46-68`). Enabled providers are entries whose `enabled` value or metadata default is true (`Sources/CodexBarCore/Config/CodexBarConfig.swift:74-81`). In the app, enabled providers are ordered through settings, then filtered by availability for background work (`Sources/CodexBar/UsageStore.swift:424-439`, `Sources/CodexBar/UsageStore.swift:510-551`).

## 2. Config

Config location resolution order is exact: `CODEXBAR_CONFIG` override; absolute `$XDG_CONFIG_HOME/codexbar/config.json`; existing `~/.config/codexbar/config.json`; existing legacy `~/.codexbar/config.json`; otherwise new default `~/.config/codexbar/config.json` (`docs/configuration.md:14-21`, `Sources/CodexBarCore/Config/CodexBarConfigStore.swift:20-23`, `Sources/CodexBarCore/Config/CodexBarConfigStore.swift:76-116`). Relative `XDG_CONFIG_HOME` is ignored (`docs/configuration.md:16-17`, `Sources/CodexBarCore/Config/CodexBarConfigStore.swift:88-98`). The directory is created if missing and writes are atomic, pretty-printed, sorted-key JSON (`Sources/CodexBarCore/Config/CodexBarConfigStore.swift:53-68`). On macOS/Linux, CodexBar sets file permissions to `0600` when writing (`docs/configuration.md:20-21`, `Sources/CodexBarCore/Config/CodexBarConfigStore.swift:118-124`).

Root schema:

```json
{
  "version": 1,
  "providers": [
    {
      "id": "codex",
      "enabled": true,
      "source": "auto",
      "extrasEnabled": null,
      "apiKey": null,
      "secretKey": null,
      "cookieHeader": null,
      "cookieSource": "auto",
      "region": null,
      "workspaceID": null,
      "enterpriseHost": null,
      "tokenAccounts": null,
      "codexActiveSource": null,
      "codexProfileHomePaths": null,
      "quotaWarnings": null,
      "kiloKnownOrganizations": null,
      "kiloEnabledOrganizationIDs": null,
      "awsProfile": null,
      "awsAuthMode": null
    }
  ]
}
```

This combines the documented example root (`docs/configuration.md:23-42`) with the full `ProviderConfig` fields in source (`Sources/CodexBarCore/Config/CodexBarConfig.swift:96-157`). All provider fields are optional except `id`; docs list `enabled`, `source`, `apiKey`, `enterpriseHost`, `cookieSource`, `cookieHeader`, `region`, `workspaceID`, and `tokenAccounts` semantics (`docs/configuration.md:44-62`). Source values are `auto|web|cli|oauth|api` (`docs/configuration.md:49-52`, `Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:8-14`). Cookie source values are `auto|manual|off`; `off` disables cookies, `auto` and `manual` are enabled (`docs/configuration.md:55-57`, `Sources/CodexBarCore/Providers/ProviderCookieSource.swift:3-25`). Manual cookie headers expect an HTTP `Cookie:` header value, not a Netscape cookie export (`docs/configuration.md:63-74`).

`tokenAccounts` schema:

```json
{
  "version": 1,
  "activeIndex": 0,
  "accounts": [
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "label": "user@example.com",
      "token": "sk-...",
      "addedAt": 1735123456,
      "lastUsed": 1735220000,
      "externalIdentifier": null,
      "usageScope": null,
      "organizationId": null,
      "workspaceID": null
    }
  ]
}
```

The first seven fields are documented (`docs/configuration.md:151-166`); source adds optional `externalIdentifier`, `usageScope`, `organizationId`, and `workspaceID`, with `organizationID` encoded as JSON key `organizationId` (`Sources/CodexBarCore/TokenAccounts.swift:3-32`, `Sources/CodexBarCore/TokenAccounts.swift:78-93`). A separate legacy token-account file exists at Application Support `CodexBar/token-accounts.json`, schema `{ version: 1, providers: { <providerId>: ProviderTokenAccountData } }`, written with `0600` on macOS (`Sources/CodexBarCore/TokenAccounts.swift:95-164`).

`codexActiveSource` schema is tagged: `{ "kind": "liveSystem" }`, `{ "kind": "managedAccount", "accountID": "<uuid>" }`, or encoded profile-home compatibility shape `{ "kind": "liveSystem", "homePath": "<path>" }`; decoding also accepts `{ "kind": "profileHome", "homePath": "<path>" }` (`Sources/CodexBarCore/Config/CodexActiveSource.swift:3-59`).

`quotaWarnings` schema is `{ "session": { "thresholds": [Int], "enabled": Bool }, "weekly": { "thresholds": [Int], "enabled": Bool } }`. Defaults are `[50, 20]`; allowed threshold range is `0...99`; values are clamped, deduplicated, sorted descending, and empty input falls back to defaults (`Sources/CodexBarCore/Config/CodexBarConfig.swift:205-309`).

Validation rules: version must equal `1`; provider `source` must be in the descriptor's `fetchPlan.sourceModes`; `apiKey` without API support warns; `source: api` without API support errors; `source: api` with no key warns; cookie settings on non-web providers warn; manual cookie source without a header warns; `secretKey` only applies to Bedrock and Doubao; `workspaceID` only applies to `azureopenai`, `openai`, `opencode`, `opencodego`, `devin`, and `deepgram`; `enterpriseHost` only applies to `azureopenai`, `copilot`, `kimi`, `llmproxy`, and `litellm`; unsupported token accounts warn (`Sources/CodexBarCore/Config/CodexBarConfigValidation.swift:30-178`). Region validation is provider-specific for MiniMax, z.ai, Alibaba Coding Plan, Moonshot, Bedrock, and Doubao; other providers warn on region use (`Sources/CodexBarCore/Config/CodexBarConfigValidation.swift:244-307`).

UI preferences such as refresh frequency and status polling are not in this JSON config; refresh frequency is stored in UserDefaults via `SettingsStore` (`docs/refresh-loop.md:10-12`, `Sources/CodexBar/SettingsStore+Defaults.swift:8-13`).

## 3. Refresh loop

Refresh frequency choices are exactly `manual`, `oneMinute`, `twoMinutes`, `fiveMinutes`, `fifteenMinutes`, and `thirtyMinutes`; their intervals are nil, 60, 120, 300, 900, and 1800 seconds respectively (`Sources/CodexBar/SettingsStore.swift:6-27`). Docs call 5 minutes the default (`docs/refresh-loop.md:10-12`). The timer cancels any previous timer and reset-boundary task, then if frequency has seconds, starts a detached utility task that sleeps for the fixed wait and calls `refresh()` repeatedly (`Sources/CodexBar/UsageStore.swift:713-724`).

A batch `refresh()` delegates to `runRefresh()`, exits if `isRefreshing` is already true, marks `isRefreshing`, gathers enabled providers for display/background work, and runs provider refresh work (`Sources/CodexBar/UsageStore.swift:576-608`). Background work uses enabled providers after availability filtering (`Sources/CodexBar/UsageStore.swift:424-439`). Provider refreshes are coalesced/replaced per provider: `refreshProvider(... coalesceIfRefreshing:)` can wait for existing provider state, otherwise begins a replacing request, waits for predecessors, refreshes the current generation, and marks retry-required only when cancelled without publishing a new snapshot (`Sources/CodexBar/UsageStore+Refresh.swift:58-103`). The actual provider fetch runs off MainActor through the descriptor fetch pipeline (`Sources/CodexBar/UsageStore+Refresh.swift:174-188`).

Reset-boundary refresh is implemented. After a batch, `scheduleResetBoundaryRefreshIfNeeded(normalRefreshInterval:)` runs (`Sources/CodexBar/UsageStore.swift:682-684`). Constants are `resetBoundaryRefreshGraceSeconds = 30` and `resetBoundaryRefreshMinimumDelaySeconds = 5` (`Sources/CodexBar/UsageStore.swift:107-111`). It scans all `primary`, `secondary`, `tertiary`, and `extraRateWindows` reset dates; schedules a refresh at `resetsAt + 30s` only if that boundary is before or equal to the next normal refresh, has not already been attempted, and the snapshot predates the boundary; actual run is delayed to at least now + 5s (`Sources/CodexBar/UsageStore+ResetBoundaryRefresh.swift:10-122`, `Sources/CodexBar/UsageStore+ResetBoundaryRefresh.swift:125-129`). Attempts are capped to 64 remembered boundaries (`Sources/CodexBar/UsageStore+ResetBoundaryRefresh.swift:49-55`).

Menu-open refresh is implemented separately. `defaultMenuOpenRefreshDelay` is 1.2 seconds (`Sources/CodexBar/StatusItemController+Menu.swift:10-16`). On menu open, the controller registers the open menu and calls `scheduleOpenMenuRefresh(for:)` (`Sources/CodexBar/StatusItemController+Menu.swift:130-150`). That scheduler normally refreshes only visible providers whose data is missing or stale, where stale means the last provider fetch failed; if `refreshAllProvidersOnMenuOpen` is enabled, it refreshes every enabled provider after the same delay, still as background usage-only work so prompt-capable OpenAI dashboard work stays deferred until the menu closes (`Sources/CodexBar/StatusItemController+Menu.swift:1104-1173`). The setting is UserDefaults key `refreshAllProvidersOnMenuOpen`, and the periodic refresh clock remains unchanged (`Sources/CodexBar/SettingsStore+Defaults.swift:16-23`).

Startup connectivity retry is implemented, not a general network-change listener. During startup retry-active refreshes, retryable failures set `startupConnectivityRetryNeeded`; completion schedules the next attempt if `startupConnectivityRetryDelay(forAttempt:)` returns a delay, then runs `runRefresh(startupConnectivityRetryAttempt:)` after sleeping (`Sources/CodexBar/UsageStore+StartupConnectivityRetry.swift:28-83`). In the searched scoped files, no `NWPathMonitor` or `NSWorkspace.didWakeNotification` refresh trigger was found, so wake/network-change immediate refresh should not be treated as confirmed from this slice.

Predictive/adaptive refresh is not implemented. The decision record says accepted design, no runtime impact until separately implemented (`docs/predictive-refresh-policy.md:9-23`). If ported, the approved deterministic policy adds an `Adaptive` frequency with delays: constrained low-power/serious-or-critical thermal = 30m; menu opened within 5m or future timestamp = 2m; opened >5m and <=1h = 5m; opened >1h and <4h = 15m; no menu open or >=4h = 30m (`docs/predictive-refresh-policy.md:74-122`). It explicitly excludes provider/account prediction, quota levels, error count, learned ranking, and scheduler-level failure backoff (`docs/predictive-refresh-policy.md:123-172`).

## 4. Data model

The normalized usage snapshot passed toward UI is `UsageSnapshot`. Its core fields are: `primary`, `secondary`, `tertiary`, `extraRateWindows`, `providerCost`, provider-specific detail payloads, `codexResetCredits`, subscription dates, `updatedAt`, `identity`, and `dataConfidence` (`Sources/CodexBarCore/UsageFetcher.swift:174-205`). Core rate windows are `RateWindow { usedPercent: Double, windowMinutes: Int?, resetsAt: Date?, resetDescription: String?, nextRegenPercent: Double?, isSyntheticPlaceholder: Bool }`; `remainingPercent` is derived as `max(0, 100 - usedPercent)` (`Sources/CodexBarCore/UsageFetcher.swift:3-72`). Extra named windows are `NamedRateWindow { id, title, window, usageKnown }`, with `usageKnown` defaulting true for older cached payloads (`Sources/CodexBarCore/UsageFetcher.swift:94-137`). Identity is `ProviderIdentitySnapshot { providerID, accountEmail, accountOrganization, loginMethod }` and is intentionally scoped by provider (`Sources/CodexBarCore/UsageFetcher.swift:139-165`). Data confidence values are `exact`, `estimated`, `percentOnly`, and `unknown` (`Sources/CodexBarCore/UsageFetcher.swift:167-172`).

Encoding keeps `primary`, `secondary`, and `tertiary` keys present as null when absent; optional provider-specific fields are omitted if nil; `dataConfidence` is omitted when `.unknown`; legacy identity fields `accountEmail`, `accountOrganization`, and `loginMethod` are also encoded for compatibility (`Sources/CodexBarCore/UsageFetcher.swift:207-232`, `Sources/CodexBarCore/UsageFetcher.swift:363-392`). On decode, `zaiUsage`, `minimaxUsage`, `cursorRequests`, and Command Code enrichment markers are live-only and not persisted (`Sources/CodexBarCore/UsageFetcher.swift:308-339`).

Credits use `CreditsSnapshot { remaining: Double, events: [CreditEvent], updatedAt: Date, codexCreditLimit: CodexCreditLimitSnapshot? }`; credit events are `{ id, date, service, creditsUsed }` (`Sources/CodexBarCore/CreditsModels.swift:3-57`). Codex credit limits normalize title, used, limit, remaining, remainingPercent, resetsAt, updatedAt, and derive `usedPercent` as `100 - remainingPercent` clamped to 0...100 (`Sources/CodexBarCore/CreditsModels.swift:59-90`). Codex reset credits are modeled as a list of reset credits plus `availableCount` and `updatedAt`, with statuses `available`, `redeeming`, `redeemed`, `expired`, or unknown string (`Sources/CodexBarCore/CreditsModels.swift:92-205`).

Staleness in `UsageStore` is error-based: `isStale(provider:)` returns true when `errors[provider] != nil`; a provider has satisfied usage fetch if it has a snapshot or known unavailable limits; a retry is needed when stale or unsatisfied (`Sources/CodexBar/UsageStore.swift:494-508`). Docs state stale/error states dim the icon and surface status in-menu (`docs/refresh-loop.md:14-20`).

## 5. Status polling

Status checks are controlled by UserDefaults key `statusChecksEnabled` through `SettingsStore` (`Sources/CodexBar/SettingsStore+Defaults.swift:100-105`). Docs describe the toggle as Settings -> Advanced -> "Check provider status" (`docs/status.md:14-18`). The app stores status in `statuses: [UsageProvider: ProviderStatus]` and component rows in `statusComponents` (`Sources/CodexBar/UsageStore.swift:155-164`). `ProviderStatus` is `{ indicator: ProviderStatusIndicator, description: String?, updatedAt: Date? }`; indicators are `none`, `minor`, `major`, `critical`, `maintenance`, and `unknown`, with every value except `none` considered an issue (`Sources/CodexBar/UsageStoreSupport.swift:4-35`).

For `statusPageURL`, status summary fetch first tries incident.io native grouped feed `https://<host>/proxy/<host>`, then overlays description/timestamp from Statuspage `api/v2/status.json`; if that fails it falls back to classic Statuspage `api/v2/summary.json` plus best-effort `api/v2/components.json` (`Sources/CodexBar/UsageStore+Status.swift:88-138`). The simpler status fetch endpoint is `baseURL/api/v2/status.json` with 10-second timeout, parsing `status.indicator`, `status.description`, and `page.updated_at` (`Sources/CodexBar/UsageStore+Status.swift:48-86`). Component status strings map as: `operational -> none`, `degraded_performance -> minor`, `partial_outage -> major`, `major_outage` or `full_outage -> critical`, `under_maintenance -> maintenance`, otherwise `unknown` (`Sources/CodexBar/UsageStoreSupport.swift:58-79`).

For Google Workspace incidents, the endpoint is exactly `https://www.google.com/appsstatus/dashboard/incidents.json`, 10-second timeout (`docs/status.md:19-23`, `Sources/CodexBar/UsageStore+Status.swift:353-368`). The parser filters active incidents relevant to the metadata `statusWorkspaceProductID`, selects the highest-ranked active incident, summarizes its update text or external description, and sets updatedAt from most recent update, modified time, or begin time (`Sources/CodexBar/UsageStore+Status.swift:370-399`). Workspace status/severity maps: `AVAILABLE -> none`, `SERVICE_INFORMATION -> minor`, `SERVICE_DISRUPTION -> major`, `SERVICE_OUTAGE -> critical`, `SERVICE_MAINTENANCE` or `SCHEDULED_MAINTENANCE -> maintenance`; severity fallback `low -> minor`, `medium -> major`, `high -> critical`, default minor (`Sources/CodexBar/UsageStore+Status.swift:401-428`).

Provider metadata decides status behavior: if `statusPageURL` exists, fetch Statuspage/incident.io summary; else if `statusWorkspaceProductID` exists, fetch Google Workspace; else no polling. On errors, previous status is kept to avoid flapping, and if no previous status exists the provider gets unknown with the error description (`Sources/CodexBar/UsageStore.swift:911-945`). Docs specify OpenAI, Claude, Cursor, Factory, and Copilot use Statuspage.io; Gemini and Antigravity use Google Workspace incidents for Gemini (`docs/status.md:10-13`). If only `statusLinkURL` exists, menu action opens it without polling (`docs/status.md:24-27`).

## 6. Other load-bearing details

Logging/diagnostics are first-class seams. Provider fetch outcomes retain attempts and errors for debug UI and CLI verbose output (`docs/provider.md:68-70`, `Sources/CodexBarCore/Providers/ProviderFetchPlan.swift:132-153`). Provider authoring docs require host APIs including `LoggerAPI` with redaction helpers and say providers should not directly access FileManager, Security, or browser internals unless they implement the host API (`docs/provider.md:71-83`).

CLI version detection is descriptor-driven through `ProviderCLIConfig.versionDetector`; `UsageStore` stores versions per provider in `versions: [UsageProvider: String]` (`Sources/CodexBarCore/Providers/ProviderCLIConfig.swift:3-17`, `Sources/CodexBar/UsageStore.swift:155-158`). The descriptor registry builds CLI name/alias maps from every descriptor (`Sources/CodexBarCore/Providers/ProviderDescriptor.swift:155-165`).

Browser cookie defaults are macOS-specific and become nil off macOS. Default import order is `Browser.defaultImportOrder` on macOS and nil otherwise; several providers override order, e.g. Codex prefers Safari/Chrome/Firefox first, Grok/Devin/Copilot/Qoder are Chrome-only, Cursor prefers Safari first (`Sources/CodexBarCore/Providers/Providers.swift:201-278`). This is load-bearing for Windows because cookie import behavior cannot be copied as-is.

Privacy/identity guardrails are explicit: identity fields are email/org/plan/loginMethod and must stay siloed per provider; default to on-device parsing; cookies are opt-in and not persisted beyond WebKit stores; providers must be timeout-bounded; cached data should be preferred over flapping; errors should be clear when stale (`docs/provider.md:19-24`, `docs/provider.md:156-160`).

---
Note on execution: Codex's first attempt (`-s read-only`) failed to launch the sandbox on this Windows machine with `windows sandbox: CreateProcessWithLogonW failed: 5` before reading any files. The retry with `-s workspace-write` succeeded and produced the analysis above (all reads were non-destructive; no repo files were modified as part of this read-only analysis task).
