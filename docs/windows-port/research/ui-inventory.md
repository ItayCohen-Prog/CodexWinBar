# UI/UX feature inventory

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Key facts

- Default status presentation is merged mode because `mergeIcons` defaults to true (Sources/CodexBar/SettingsStore.swift).
- The status gauge renderer is 18 x 18 pt, 36 x 36 px at 2x, and the status item is variable width (docs/ui.md, Sources/CodexBar/IconRenderer.swift).
- Percent fill means remaining by default; `usageBarsShowUsed` flips both menu bars and icon bars to used percent (default false).
- Brand+percent taskbar/menu-bar mode (`menuBarShowsBrandIconWithPercent`) is optional and defaults off; display text modes are percent, pace, both, and reset time.
- Loading animation is bounded at 30 FPS (phase increment 2.7/30) with a 30 second maximum continuous duration (Sources/CodexBar/StatusItemController+Animation.swift).
- Merged menu includes an Overview tab only when selected Overview providers exist, capped at 3 providers (Sources/CodexBar/PreferencesDisplayPane.swift).
- Menu cards render header, usage metrics, credits, provider/token cost, and notes/extras when data exists, in that order (Sources/CodexBar/MenuCardView.swift).
- Usage metrics can include primary, secondary, tertiary, and extra windows; extra Codex windows obey the optional credits/extra usage setting.
- Settings panes are General, Display, Providers, Advanced, Debug, and About (Sources/CodexBar/PreferencesView.swift and pane files).
- Refresh cadence options are Manual, 1 min, 2 min, 5 min, 15 min, and 30 min; default is 5 minutes.
- Status checks default enabled; launch at login defaults disabled; refresh-on-open defaults disabled.
- Quota notifications include session depleted/restored (threshold 0.0001) and threshold-based session/weekly warnings, with sound (Glass/Ping), on-screen overlay, and internal notification post options (Sources/CodexBar/SessionQuotaNotifications.swift).
- Base menu-card width is 310 pt (Sources/CodexBar/StatusItemController+Menu.swift); usage bars are 6 pt high.
- No universal green/amber/red threshold palette was found for main usage bars in the inspected files — provider brand color is the primary fill tint; pace stripes use green/red semantics separately.
- Codex exec's read-only sandbox mode (`-s read-only`) failed to launch on this Windows host with `CreateProcessWithLogonW failed: 5`; the successful run used `-s workspace-write` (no files were modified, task was read-only analysis).

## Windows portability

**Taskbar Status Item**

- Map merged/per-provider macOS `NSStatusItem.variableLength` to a Windows taskbar-adjacent widget/toolbar surface. Natural implementation choices are a WinUI 3 window hosted near the taskbar using Windows App SDK, or a taskbar deskband-style integration if pursuing shell extension behavior. Preserve the two modes: one merged taskbar item with switcher flyout, or one compact item per enabled provider.
- Render the 18 pt macOS gauge as a DPI-aware vector/raster taskbar glyph, targeting 16-20 effective pixels plus optional text. Keep percent text as taskbar label in brand/percent mode.
- Brand+text mode maps naturally to provider glyph + text inside a compact taskbar chip. Text modes: percent, pace, percent + pace, reset countdown.
- Incident overlays map to a small Fluent badge/dot on the provider glyph: dot for minor/maintenance, exclamation badge for major/critical/unknown.
- Loading animation should be capped at 30 FPS and stopped after 30 seconds, matching CodexBar behavior. Use CompositionTarget.Rendering, DispatcherTimer, or WinUI Composition animations with a hard timeout.

**Flyout / Menu**

- Map the AppKit `NSMenu` + SwiftUI card to a WinUI 3 `Flyout`/`TeachingTip`-style anchored panel from the taskbar widget. Use a fixed baseline width around 310 macOS points translated to roughly 320-360 effective pixels on Windows, with responsive widening only for localization.
- Merged provider switcher maps to a Fluent segmented control or tab row. Include Overview tab only when up to 3 Overview providers are selected.
- Provider cards map to Fluent grouped panels inside the flyout: header, usage bars, credits, costs, status incidents, action row.
- Usage bars map to WinUI `ProgressBar` or custom `Canvas` for pace stripes and threshold markers. Preserve 6 px-ish compact height, provider-color fill, warning markers, and left/right detail labels.
- Persistent Refresh maps to a command row/button with `Refresh` icon and inline `ProgressRing`; Settings maps to gear command opening the Settings window; Quit maps to app-exit command.
- Error copy buttons map to small icon buttons with `Copy` icon and tooltip; stale/unavailable/unfetched states should remain visible in subtitles.

**Settings Window**

- Map SwiftUI Settings panes to a WinUI 3 `NavigationView` settings window with pages: General, Display, Providers, Advanced, Debug, About.
- General page: language `ComboBox`, terminal app `ComboBox` or Windows terminal profile picker, launch at login toggle using Windows StartupTask or registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` depending packaging, refresh cadence combo, refresh-on-open toggle, status-check toggle, notifications toggles, quit button.
- Display page: merge icons toggle, switcher icons toggle, highest-usage toggle, Overview provider picker dialog, hide critters toggle, brand+percent toggle, display mode combo, show-used toggle, warning markers toggle, weekly work-days combo, absolute reset-time toggle, provider changelog toggle, optional credits/extra usage toggle, multi-account layout combo, cost summary options, animation/confetti toggles.
- Providers page: provider list with enable toggles, search/filter, reorder unless alphabetical sort is active, detail page per provider with auth/API/cookie/token fields, refresh button, error copy, token accounts, Codex managed accounts.
- Advanced page: disable secure credential access analogue, storage-footprint scans, JetBrains path, logging/debug controls. On Windows, use Windows Credential Manager or DPAPI-protected local storage instead of Keychain; browser cookie import should default to Chrome/Edge and avoid prompts.

**Notifications**

- Map session depleted/restored and quota warning notifications to Windows toast notifications via Windows App SDK notifications or WinRT toast APIs. Preserve provider/window/threshold/account copy inputs.
- Map optional sound to toast sound or local sound playback; map on-screen alert overlay to a topmost lightweight WinUI notification overlay only if setting is enabled.
- Map status-item quota flash to a temporary badge/accent flash on the taskbar widget for 60 seconds.

**Windows 11 Fluent Translation**

- Typography: use Segoe UI/Segoe Fluent Icons equivalents: title/body/caption hierarchy matching headline/body/footnote.
- Colors: use provider brand color for usage fills, Fluent neutral track color, red/error for failures, green/red pace stripe semantics, and theme-aware foreground/background resources.
- Dark/light: bind to `Application.RequestedTheme`/system theme and WinUI theme resources; avoid hardcoded macOS vibrant-menu assumptions.
- App icon pipeline should be replaced with Windows `.ico`/MSIX asset generation; the macOS `.icon`/`.icns` pipeline is not portable.

## Open questions

- Provider-specific API key/cookie field names are delegated to each `ProviderCatalog` implementation; not enumerated for all 50+ providers per the surgical-scope constraint.
- Exact localized display strings were cited by key from Swift source, not read from actual .strings/.xcstrings localization resource files.
- No universal green-to-amber-to-red usage-bar threshold palette was found in the inspected files; needs confirmation whether such logic exists elsewhere (e.g., in a Localizable strings file or a not-yet-inspected color helper).
- The exact first-run/onboarding flow (e.g., a welcome window or permission-request sequence) was not fully visible in the headline files inspected; only persisted `providerDetectionCompleted` state and empty-state text strings were confirmed.
- Why did codex exec's `-s read-only` sandbox fail with `CreateProcessWithLogonW failed: 5` on this Windows machine — worth investigating for future read-only-only tasks (may require elevated token/permissions).

## Full report

**1. Status Item / Bar Icon**

- CodexBar is an LSUIElement menu-bar app with no Dock icon; its status item is an AppKit `NSStatusItem` using a custom `NSImage` (`docs/ui.md`, `Sources/CodexBar/StatusItemController.swift`).
- Status items are variable-width: `statusBar.statusItem(withLength: NSStatusItem.variableLength)`, with stable autosave names `codexbar-merged` or `codexbar-<provider>` so macOS preserves placement (`Sources/CodexBar/StatusItemController.swift`).
- Button image scaling is `.scaleNone` to keep the rendered icon crisp at 1:1 (`Sources/CodexBar/StatusItemController.swift`).
- Default mode is one merged icon because `mergeIcons` defaults to `true`; per-provider status items exist when merge is off (`Sources/CodexBar/SettingsStore.swift`, `Sources/CodexBar/StatusItemController.swift`).
- The renderer outputs an 18 x 18 pt template image, 36 x 36 px at 2x (`docs/ui.md`, `Sources/CodexBar/IconRenderer.swift`).
- Icon gauge mode renders primary and secondary provider windows as compact bars. The fill value is percent remaining by default; `usageBarsShowUsed` flips it to percent used and defaults to `false` (`docs/ui.md`, `Sources/CodexBar/StatusItemController+Animation.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Brand/percent mode is optional: `menuBarShowsBrandIconWithPercent` defaults to `false`; when enabled, the button image is the provider brand icon and the button title is `menuBarDisplayText` (`docs/ui.md`, `Sources/CodexBar/StatusItemController+Animation.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Brand text modes are `percent`, `pace`, `both`, and `resetTime`; default is `percent` (`Sources/CodexBar/MenuBarDisplayMode.swift`, `Sources/CodexBar/MenuBarDisplayText.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Combined session/weekly text exists for providers with both lanes, formatted like `5h 78% · W 42%` using `combinedSessionWeeklyPercentText` (`docs/ui.md`, `Sources/CodexBar/MenuBarDisplayText.swift`).
- Codex menu-bar metric supports automatic/primary, secondary/tertiary, extra usage, average, and primary+secondary behavior; average uses `(primary.usedPercent + secondary.usedPercent) / 2`, and primary+secondary chooses the higher used-percent window (`Sources/CodexBar/StatusItemController.swift`).
- Loading animation runs at 30 FPS, phase increment `2.7 / 30`, with a maximum continuous duration of 30 seconds (`docs/ui.md`, `Sources/CodexBar/StatusItemController+Animation.swift`).
- Loading animation uses `LoadingPattern` values such as `.knightRider`; during loading, normal snapshot percentages can be replaced by pattern values while stale state is suppressed (`Sources/CodexBar/StatusItemController.swift`, `Sources/CodexBar/StatusItemController+Animation.swift`).
- Stale/failed refresh state dims gauge icons via reduced track/fill alpha (`docs/ui.md`, `Sources/CodexBar/IconRenderer.swift`).
- Status incidents are represented by `ProviderStatusIndicator`; minor/maintenance in brand mode draw a 4 x 4 dot at the lower-right, while major/critical/unknown draw a small exclamation-like mark (`docs/ui.md`, `Sources/CodexBar/StatusItemController+Animation.swift`, `Sources/CodexBar/IconRenderer.swift`).
- Quota warnings can flash the status item for 60 seconds via `quotaWarningFlashDuration` and `quotaWarningFlashUntil` (`Sources/CodexBar/StatusItemController.swift`).

**2. Popover / Menu**

- The menu is an AppKit `NSMenu` populated by `StatusItemController`; base menu-card width is 310 pt (`Sources/CodexBar/StatusItemController+Menu.swift`).
- Merged mode opens a base menu with a provider switcher. When Overview has selected providers, the switcher includes an Overview tab showing up to 3 provider rows; rows follow provider order and selecting a row jumps to the provider detail card (`docs/ui.md`, `Sources/CodexBar/PreferencesDisplayPane.swift`).
- Per-provider mode opens provider-specific menus; fallback provider is the first enabled provider or Codex (`Sources/CodexBar/StatusItemController+Menu.swift`).
- Menu refresh behavior: opening a menu can schedule a delayed refresh after 1.2 seconds, track open menus, and avoid rebuilding geometry during in-flight updates (`Sources/CodexBar/StatusItemController+Menu.swift`).
- Header structure: provider name in headline/semibold, plan/email/status subtitle beneath, with error subtitles styled differently and preserving measured row geometry (`Sources/CodexBar/MenuCardView.swift`).
- Provider detail card sections render in this order when available: usage metrics, credits, provider cost, token usage/cost estimate, notes/extra content (`Sources/CodexBar/MenuCardView.swift`).
- Usage metrics include primary, secondary, tertiary, and extra windows when the snapshot has data. Extra windows are skipped for Codex when optional credits/extra usage is disabled and skipped for Copilot when budget extras are disabled (`docs/ui.md`, `Sources/CodexBar/MenuCardView+ModelHelpers.swift`, `Sources/CodexBar/StatusItemController+MenuCardModel.swift`).
- Window labels are provider-specific: e.g. Factory uses `5-hour`, `Weekly`, `Monthly`; Cursor can label primary as `Requests`; otherwise it uses provider metadata session label (`Sources/CodexBar/MenuCardView+ModelHelpers.swift`).
- Usage rows contain a title, a 6 pt high progress bar, a percent label, optional status/reset text, optional left/right detail labels, optional pace marker, and optional quota warning markers (`Sources/CodexBar/MenuCardView.swift`, `Sources/CodexBar/UsageProgressBar.swift`).
- Reset text uses countdown by default; `resetTimesShowAbsolute` switches to absolute clock display and defaults to `false` (`docs/ui.md`, `Sources/CodexBar/StatusItemController+MenuCardModel.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Extra reset formatting includes strings such as `Resets in %@` for Antigravity quota summaries and synthetic regen text like `Regenerates %@` (`Sources/CodexBar/MenuCardView+ModelHelpers.swift`).
- Pace tracking appears where enough timing data exists; deficit displays run-out timing, reserve/on-pace displays headroom or reset survival copy (`docs/ui.md`, `Sources/CodexBar/MenuCardView+ModelHelpers.swift`, `Sources/CodexBar/UsageProgressBar.swift`).
- Credits render as a separate credits bar/content block when `creditsText` exists, with `creditsRemaining`, `creditsProgressPercent`, scale text, hints, and click-to-copy hint support (`docs/ui.md`, `Sources/CodexBar/MenuCardView.swift`).
- Codex credits can add a separate `Buy Credits...` menu action (`docs/ui.md`).
- Cost/token rows exist when token-cost usage is enabled; token usage section has session/month/hint/error lines and copyable error text (`Sources/CodexBar/MenuCardView.swift`, `Sources/CodexBar/StatusItemController+MenuCardModel.swift`).
- Status incident rows/components are attached as menu adjuncts/submenus through status component IDs and provider status checks; status checks default to enabled (`Sources/CodexBar/StatusItemController+Menu.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Persistent menu actions include Refresh, Settings, and Quit with shortcuts Cmd-R, Cmd-comma, and Cmd-Q; refresh uses a persistent row that can show spinner/disabled state without changing geometry (`Sources/CodexBar/StatusItemController+Menu.swift`, `docs/ui.md`).
- Loading state uses subtitle style `.loading`; error state uses subtitle style `.error`, red/error styling, and copy buttons for error text (`Sources/CodexBar/MenuCardView.swift`).
- Stale state is surfaced as `last_fetch_failed` in provider subtitles and dims the menu-bar icon (`Sources/CodexBar/PreferencesProvidersPane.swift`, `Sources/CodexBar/IconRenderer.swift`).
- Empty/unfetched state text includes `usage_not_fetched_yet`; unavailable limits use `Limits not available` (`Sources/CodexBar/PreferencesProvidersPane.swift`).

**3. Settings UI**

- Settings are SwiftUI `Form` panes driven by `PreferencesView` with panes including General, Display, Providers, Advanced, Debug, and About based on source filenames and pane implementations (`Sources/CodexBar/PreferencesView.swift`, `Sources/CodexBar/PreferencesGeneralPane.swift`, `Sources/CodexBar/PreferencesDisplayPane.swift`, `Sources/CodexBar/PreferencesProvidersPane.swift`, `Sources/CodexBar/PreferencesAdvancedPane.swift`).
- General pane, System section: Language picker, Terminal app picker, Launch at login toggle. Launch at login defaults to `false` (`Sources/CodexBar/PreferencesGeneralPane.swift`, `Sources/CodexBar/SettingsStore.swift`).
- General pane, Refresh section: Refresh cadence picker with Manual, 1 min, 2 min, 5 min, 15 min, 30 min; default is 5 minutes. Also includes Refresh on menu open toggle default `false` and provider status checks toggle default `true` (`Sources/CodexBar/SettingsStore.swift`, `Sources/CodexBar/PreferencesGeneralPane.swift`).
- General pane, Notifications section: session quota notifications toggle, quota warning notifications toggle, and global quota-warning settings when enabled (`Sources/CodexBar/PreferencesGeneralPane.swift`).
- General pane includes a Quit app button (`Sources/CodexBar/PreferencesGeneralPane.swift`).
- Display pane, Menu bar section: Merge Icons toggle default `true`; switcher shows icons toggle default `true`; show most-used provider toggle default `false`; Overview tab providers selector; Hide critters toggle default `false`; brand icon with percent toggle default `false`; display mode picker default `percent` (`Sources/CodexBar/PreferencesDisplayPane.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Display pane, Menu content section: Show usage as used toggle default `false`; quota warning markers visible default `true`; weekly progress work days picker Off/4/5/7 default unset; show reset time as clock default `false`; provider changelog links default `false`; show optional credits and extra usage default `true`; multi-account menu layout picker (`Sources/CodexBar/PreferencesDisplayPane.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Display pane also includes cost summary settings, random blink default `false`, session-limit confetti and weekly-limit confetti settings loaded from reset defaults (`Sources/CodexBar/PreferencesDisplayPane.swift`, `Sources/CodexBar/SettingsStore.swift`).
- Overview tab providers: configurable only when Merge Icons is enabled and active providers exist; selector is a popover of checkbox toggles, max 3 selected, disabled when max is reached (`docs/ui.md`, `Sources/CodexBar/PreferencesDisplayPane.swift`).
- Providers pane is provider-detail based: enable toggle binding, subtitle with CLI/source/version and last fetch state, menu-card preview model, extra provider-specific pickers/toggles/fields/actions/organizations, token accounts, error display with copy, and refresh action (`Sources/CodexBar/PreferencesProvidersPane.swift`).
- Provider-specific settings are delegated to `ProviderCatalog.implementation(for:)` via `settingsToggles`, `settingsPickers`, `settingsFields`, `settingsActions`, and `settingsOrganizations`; this means exact provider settings vary by provider implementation (`Sources/CodexBar/PreferencesProvidersPane.swift`).
- Codex provider settings include managed account section with visible accounts, active account, live account, add account, reauthenticate, remove, and promote/request system-visible account flows (`Sources/CodexBar/PreferencesProvidersPane.swift`).
- Token-account providers get token account management with label/token fields, active index, optional Claude organization field, optional Z.ai team mode controls, and account refresh on active-account changes (`Sources/CodexBar/PreferencesProvidersPane.swift`).
- Advanced includes Disable Keychain access and Show provider storage usage per docs. Disable Keychain access turns off browser cookie import; storage usage enables background scans of provider-owned local paths without deleting files (`docs/ui.md`, `Sources/CodexBar/SettingsStore.swift`).
- Advanced/provider storage defaults: `providerStorageFootprintsEnabled` default `false`; `debugDisableKeychainAccess` is loaded through keychain access gate; JetBrains IDE base path defaults to empty string (`docs/ui.md`, `Sources/CodexBar/SettingsStore.swift`).
- Claude-specific settings include `claudeOAuthKeychainPromptMode`, `claudeOAuthKeychainReadStrategy`, and `claudeWebExtrasEnabled`, defaulting to nil/nil/false (`docs/ui.md`, `Sources/CodexBar/SettingsStore.swift`).
- Cost usage defaults: token cost usage disabled, history days default 30 clamped to 1...365 (`Sources/CodexBar/SettingsStore.swift`).
- OpenAI web extras defaults: `openAIWebAccessEnabled` false and `openAIWebBatterySaverEnabled` false (`Sources/CodexBar/SettingsStore.swift`).
- Provider detection completion defaults to false; providers-sorted-alphabetically defaults to false (`Sources/CodexBar/SettingsStore.swift`).

**4. Onboarding / First-Run / Empty States**

- Provider detection completion is tracked by `providerDetectionCompleted` and defaults to `false`, indicating first-run provider discovery state exists (`Sources/CodexBar/SettingsStore.swift`).
- If no provider snapshot exists, provider subtitles show `usage_not_fetched_yet`; if limits are unavailable, they show `Limits not available`; if last fetch failed, they show `last_fetch_failed` (`Sources/CodexBar/PreferencesProvidersPane.swift`).
- Merge Overview empty states: if Merge Icons is off, subtitle is `overview_enable_merge_icons_hint`; if no active providers exist, subtitle is `overview_no_providers_hint`; if no Overview providers selected, summary is `overview_no_providers_selected`; if none selected, Overview tab is hidden (`docs/ui.md`, `Sources/CodexBar/PreferencesDisplayPane.swift`).
- Fallback menu/provider behavior uses first enabled provider or Codex when no specific provider is available (`Sources/CodexBar/StatusItemController+Menu.swift`, `Sources/CodexBar/StatusItemController+MenuCardModel.swift`).

**5. Visual Design Language**

- Native macOS AppKit/SwiftUI: AppKit `NSMenu` shell with SwiftUI-hosted card rows; menu cards use constrained width 310 pt (`Sources/CodexBar/StatusItemController+Menu.swift`, `Sources/CodexBar/MenuCardView.swift`).
- Typography: provider header uses `.headline` semibold; metric titles use `.body` medium; percentages/details/errors use `.footnote`; secondary labels use `secondaryLabelColor` in AppKit menu rows (`Sources/CodexBar/MenuCardView.swift`, `Sources/CodexBar/StatusItemController+Menu.swift`).
- Layout: vertical card layout, zero top-level spacing around header/content, 12 pt section spacing, 6 pt metric internal spacing, progress bars are 6 pt high (`Sources/CodexBar/MenuCardView.swift`, `Sources/CodexBar/UsageProgressBar.swift`).
- Progress bars use provider branding color from `ProviderDescriptorRegistry.descriptor(for: provider).branding.color`; ElevenLabs uses label color instead (`Sources/CodexBar/MenuCardView+ModelHelpers.swift`).
- The inspected files do not define a universal green-to-amber-to-red threshold palette for the main usage bars; provider brand color is the default tint, with pace stripe green/red and warning markers using primary/white opacity (`Sources/CodexBar/MenuCardView+ModelHelpers.swift`, `Sources/CodexBar/UsageProgressBar.swift`).
- Error text uses `MenuHighlightStyle.error`; stale icon rendering reduces alpha (`Sources/CodexBar/MenuCardView.swift`, `Sources/CodexBar/IconRenderer.swift`).
- Highlighted menu rows adapt progress track/tint/marker colors via `MenuHighlightStyle`, including white warning markers on highlighted rows (`Sources/CodexBar/UsageProgressBar.swift`).
- Dark/light behavior is native system appearance: menus are pinned to current system appearance rather than menu-bar vibrant appearance, and settings window appearance is refreshed through `SettingsWindowAppearance` (`Sources/CodexBar/StatusItemController+Menu.swift`, `Sources/CodexBar/PreferencesView.swift`).
- App icon pipeline uses macOS `.icon` to `.icns`, rendering 824 x 824 PNG, padding to 1024 x 1024, then iconutil; this is app icon branding, not status-item renderer behavior (`docs/icon.md`).

**6. Notifications**

- Session quota notifications exist and are enabled by a General setting; logic detects transitions from non-depleted to depleted and depleted to restored with depletion threshold `0.0001` (`Sources/CodexBar/SessionQuotaNotifications.swift`, `Sources/CodexBar/PreferencesGeneralPane.swift`).
- Session notification copy uses localized keys `session_depleted_notification_title/body` and `session_restored_notification_title/body` with provider name (`Sources/CodexBar/SessionQuotaNotifications.swift`).
- Quota warning notifications exist for session and weekly windows; they fire when remaining percent crosses configured thresholds not already fired (`Sources/CodexBar/SessionQuotaNotifications.swift`).
- Quota warning body can include account display name, remaining percent, threshold, and window label; windows localize as session/weekly (`Sources/CodexBar/SessionQuotaNotifications.swift`).
- Quota warnings can play `Glass` or `Ping`, optionally show an on-screen alert overlay, post an internal `codexbarQuotaWarningDidPost` notification, and enqueue a user notification through `AppNotifications.shared.post` (`Sources/CodexBar/SessionQuotaNotifications.swift`).
- Quota-warning marker visibility defaults to true; optional notification/sound/on-screen settings are loaded from quota-warning defaults (`Sources/CodexBar/SettingsStore.swift`).

Note on execution: This analysis was produced by Codex (gpt-5.5, reasoning effort low) via `codex exec` against the local repository at C:\dev\01-active-projects\CodexWinBar, reading `docs/ui.md`, `docs/icon.md`, and the headline Sources/CodexBar/ files cited above. The read-only sandbox mode failed to launch on this Windows host (`CreateProcessWithLogonW failed: 5`), so the run was completed under workspace-write sandbox mode with no files modified for this read-only task.
