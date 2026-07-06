# Repository Guidelines

CodexWinBar is a native **Windows 11** rebuild of [steipete/CodexBar](https://github.com/steipete/CodexBar):
it puts AI coding-provider usage limits on the taskbar. **C# / .NET 9**, WPF flyout + raw Win32 taskbar
widget. The macOS/Swift tree (`Sources/`, `Tests/CodexBarTests`, `Scripts/`, `Package.swift`, `*.xcodeproj`,
appcast, etc.) is inherited upstream baggage — **ignore it**; all Windows work lives under `src/` and `Tests/`.

## Project Structure & Modules

- `src/CodexWinBar.Core` — models (`Models/`), CodexBar-compatible config store (`Config/`), fetch pipeline
  + provider descriptors (`Providers/`), refresh scheduler (`Scheduling/`), status poller (`Status/`).
  Provider brand logos are embedded PNGs under `Assets/logos/`.
- `src/CodexWinBar.Providers` — one folder per provider (Codex, Claude, OpenRouter, OpenAIAdmin, Copilot,
  Gemini, Zai, Cursor). A provider = a **descriptor + fetch strategy + parser**; register it in
  `ProviderCatalog.cs` and add its id to `Core/Providers/ProviderId.cs`.
- `src/CodexWinBar.Widget` — pure Win32: taskbar interop (`TaskbarInterop.cs`), embed/overlay state machine
  on a dedicated STA thread (`WidgetWindow.cs`), GDI+ → `UpdateLayeredWindow` renderer (`WidgetRenderer.cs`).
- `src/CodexWinBar.App` — WPF shell: flyout (`Flyout/`), settings (`Settings/`), tray (`Tray/`),
  notifications, single-instance mutex, and the composition root / entry point (`Program.cs`). Dev-only
  `Dev/FakeUsageStore.cs` serves synthetic data.
- `src/CodexWinBar.Cli` — headless CLI (`usage`, `config`, `diagnose`, `serve`).
- `Tests/CodexWinBar.Core.Tests`, `Tests/CodexWinBar.Providers.Tests` — xUnit.
- `build/pack-windows.ps1` — builds the Velopack installer. `packaging/winget/` — the winget manifest.
- `docs/windows-port/` — design docs; `ARCHITECTURE.md` (§12 amendments are authoritative) and
  `CODE-SIGNING.md`. Upstream `docs/*.md` are reused verbatim as the **provider protocol source of truth**.

## Build, Test, Run

- SDK is pinned by `global.json`. Solution: `CodexWinBar.sln`. Shared settings: `Directory.Build.props`
  (`<Version>` is the single source of truth; `nullable`, `ImplicitUsings`, and **`TreatWarningsAsErrors`**
  are on — a warning fails the build).
- Build: `dotnet build CodexWinBar.sln -c Debug`. Test: `dotnet test CodexWinBar.sln -c Debug`.
- Run: `dotnet run --project src/CodexWinBar.App` or launch
  `src/CodexWinBar.App/bin/Debug/net9.0-windows10.0.19041.0/CodexWinBar.exe`.
- **Kill the running app before rebuilding** — it holds the DLLs and the build will fail with file locks:
  `Get-Process CodexWinBar -ErrorAction SilentlyContinue | Stop-Process -Force`. It is single-instance
  (a named mutex); a second launch just signals the first via the `CodexWinBar.Activate` pipe.
- **Fake data:** `CODEXWINBAR_FAKE=1` swaps in `FakeUsageStore` (synthetic data for every provider, no real
  subscriptions); `CODEXWINBAR_FAKE_COUNT=N` caps how many providers are served (exercises the widget's
  space-aware tiers and the first-run empty state). This is the way to exercise the UI end-to-end.
- Package the installer: `build/pack-windows.ps1` (needs `dotnet tool install -g vpk --version 1.2.0`).

## Coding Style & Naming

- C# 13 / .NET 9, file-scoped namespaces, 4-space indent, nullable reference types. **Explicit `this.` is
  intentional — do not remove it.** Match the surrounding file's comment density and idiom.
- **Zero external NuGet dependencies** except **Velopack** (installer/auto-update, app project only). Do not
  add dependencies or tooling without confirmation.
- Keep changes small and reuse existing helpers. New providers only need a descriptor + strategy + parser.

## Testing Guidelines

- xUnit under `Tests/` (`FeatureNameTests` with descriptive methods). **Provider parsers get defensive tests**
  — mirror the existing `*ParserTests` (drive the internal parser via `ProviderParserReflection`).
- Run `dotnet test CodexWinBar.sln` before handoff.
- The WPF flyout and the Win32 widget are hard to unit-test; verify UI changes by running the app in fake
  mode and capturing screenshots (System.Drawing capture must set per-monitor-v2 DPI awareness). The
  non-bottom-edge taskbar code is **reasoning-verified only** — Windows 11 pins the taskbar to the bottom, so
  top/left/right embedding can't be live-tested here; don't claim otherwise.
- Model names in tests/code: released models or clearly fictitious names only; never expose unreleased names.

## Commit & PR Guidelines

- Short imperative commit subjects (e.g. "Add Cursor provider", "Fix flyout dismiss"); keep commits scoped.
  Agent-authored commits end with a `Co-Authored-By:` trailer.
- **Commit/push only when asked.** This is a solo project that ships from `main`.
- **Distribution is release-driven, and fragile — read before touching it:** a `vX.Y.Z` tag triggers
  `.github/workflows/release-windows.yml`, which publishes the Velopack `Setup.exe` + update feed to a GitHub
  Release. **Never modify, replace, or delete a published release's `Setup.exe`, its tag, or the release**
  while a winget PR references that version (winget pins the URL + SHA256). Each release needs a matching
  winget manifest update (`packaging/winget/`, id `ItayCohen.CodexWinBar`); **don't edit an in-review winget
  PR** (it resets moderator review). See `packaging/winget/README.md` and `docs/windows-port/CODE-SIGNING.md`.

## Agent Notes

- **Debug non-obvious bugs by researching online first** (exact error, library/version, symptoms) before
  trial-and-error.
- **Win32 gotchas:** the widget embeds into `Shell_TrayWnd` via `SetParent` with a per-pixel-alpha layered
  window (`UpdateLayeredWindow`; for an embedded child `pptDst` must be NULL). Window classes are registered
  with instance `WndProc` delegates — they **must** be `UnregisterClassW`'d on teardown, or a GC'd delegate
  gets called and the process hard-crashes via `FailFast` (this killed the app on widget-mode changes).
- **Provider data siloing:** when rendering usage/identity for a provider, never show fields sourced from a
  different provider.
- **Config compatibility:** the config store reads/writes the same CodexBar `config.json`; unknown fields and
  providers written by the macOS app must round-trip untouched.
- Only two CI workflows are active — **Windows CI** (push to `main`) and **Release (Windows)** (`v*` tag). The
  upstream macOS/Linux workflows (CI, Release CLI, Monitor Upstream) are intentionally disabled; don't re-enable.
