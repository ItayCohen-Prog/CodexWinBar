# Contributing Guidelines

CodexWinBar is a native **Windows 11** rebuild of [steipete/CodexBar](https://github.com/steipete/CodexBar):
AI coding-provider usage limits on the taskbar. **C# / .NET 9**, a WPF flyout + a raw Win32 taskbar widget.

This guide is for **contributing changes** — how the code is laid out, how to build and test it, and how to
send a clean pull request. Contributions (and agents acting on your behalf) are welcome.

> The macOS/Swift tree (`Sources/`, `Tests/CodexBarTests`, `Scripts/`, `Package.swift`, `*.xcodeproj`, appcast,
> etc.) is inherited from the upstream fork — **ignore it**. All Windows code lives under `src/` and `Tests/`.

## Project Structure & Modules

- `src/CodexWinBar.Core` — models (`Models/`), CodexBar-compatible config store (`Config/`), provider
  descriptors + fetch pipeline (`Providers/`), refresh scheduler (`Scheduling/`), status poller (`Status/`).
  Provider brand logos are embedded PNGs under `Assets/logos/`.
- `src/CodexWinBar.Providers` — one folder per provider (Codex, Claude, OpenRouter, OpenAIAdmin, Copilot,
  Gemini, Zai, Cursor). A provider = a **descriptor + fetch strategy + parser**.
- `src/CodexWinBar.Widget` — pure Win32: taskbar interop, the embed/overlay state machine on a dedicated STA
  thread (`WidgetWindow.cs`), and the GDI+ → `UpdateLayeredWindow` renderer (`WidgetRenderer.cs`).
- `src/CodexWinBar.App` — WPF shell: flyout (`Flyout/`), settings (`Settings/`), tray (`Tray/`),
  notifications, and the entry point / composition root (`Program.cs`). `Dev/FakeUsageStore.cs` serves
  synthetic data for testing.
- `src/CodexWinBar.Cli` — headless CLI (`usage`, `config`, `diagnose`, `serve`).
- `Tests/CodexWinBar.Core.Tests`, `Tests/CodexWinBar.Providers.Tests` — xUnit.
- `docs/windows-port/` — design docs; start with `ARCHITECTURE.md`. Upstream `docs/*.md` are the **provider
  protocol source of truth**.

## Build, Test, Run

- Prereqs: Windows 11 + the .NET 9 SDK (`global.json` pins the exact version). Solution: `CodexWinBar.sln`.
  Shared settings live in `Directory.Build.props` — note **`TreatWarningsAsErrors` is on**, so any warning
  fails the build.
- Build: `dotnet build CodexWinBar.sln`  ·  Test: `dotnet test CodexWinBar.sln`
- Run: `dotnet run --project src/CodexWinBar.App`, or launch the built
  `src/CodexWinBar.App/bin/Debug/net9.0-windows10.0.19041.0/CodexWinBar.exe`.
- **Kill the running app before rebuilding** — it holds the DLLs and the build fails with file locks:
  `Get-Process CodexWinBar -ErrorAction SilentlyContinue | Stop-Process -Force`.
- **Test with fake data** (no real subscriptions needed): set `CODEXWINBAR_FAKE=1` to serve synthetic usage
  for every provider; `CODEXWINBAR_FAKE_COUNT=N` caps how many providers show (exercises the widget's
  space-aware tiers and the first-run empty state). This is how to exercise the UI end-to-end.

## Coding Style

- C# 13 / .NET 9, file-scoped namespaces, 4-space indent, nullable reference types. **Explicit `this.` is
  intentional — keep it.** Match the surrounding file's comment density and idiom.
- **No new external NuGet dependencies** (the app ships with only Velopack, for install/update). If a change
  seems to need one, open an issue first.
- Keep changes small and focused, and reuse existing helpers.

## Making a change

- **Add a provider:** create a folder under `src/CodexWinBar.Providers/<Name>/` with a descriptor + fetch
  strategy + parser (copy an existing one, e.g. `OpenRouter/` or `Cursor/`), register it in
  `ProviderCatalog.cs`, and add its id to `Core/Providers/ProviderId.cs`. Provider fetch logic should match
  upstream CodexBar's technique for that provider (endpoints, auth, field parsing) — that's the correctness bar.
- **Parsers get tests:** mirror the existing `*ParserTests` under `Tests/CodexWinBar.Providers.Tests`.
- **UI changes** (flyout/widget/settings): verify by running the app in fake mode and capturing before/after
  screenshots — these are hard to unit-test.

## Submitting a pull request

1. **Fork** the repo and work on a **topic branch** (not `main`).
2. Make small, scoped commits with short imperative subjects (e.g. "Add Windsurf provider", "Fix flyout
   dismiss on rapid switch").
3. Before opening the PR, make sure **`dotnet test CodexWinBar.sln` passes** with no warnings.
4. Open the PR against `main` with: a short **summary** of what changed and why, the **commands you ran**, and
   for any UI change a **screenshot or GIF** (run in fake mode: `CODEXWINBAR_FAKE=1`). Link a related issue if
   there is one.
5. **Don't** bump the version or touch releases/packaging/CI (`build/`, `packaging/`, `.github/workflows/`) —
   the maintainer handles releasing and the winget/installer pipeline.

## Notes for agents & contributors

- **Debug non-obvious bugs by researching online first** (the exact error, library/version, symptoms) before
  trial-and-error.
- **Win32 gotchas** (if you touch `src/CodexWinBar.Widget`): the widget embeds into `Shell_TrayWnd` via
  `SetParent` with a per-pixel-alpha layered window (`UpdateLayeredWindow`; for an embedded child `pptDst`
  must be NULL). Window classes registered with instance `WndProc` delegates **must** be `UnregisterClassW`'d
  on teardown, or a garbage-collected delegate is invoked and the process hard-crashes via `FailFast`.
- **Provider data siloing:** when rendering usage/identity for one provider, never show fields sourced from
  another provider.
- **Config compatibility:** the config store reads/writes the same CodexBar `config.json` as the macOS app —
  unknown fields and providers must round-trip untouched.
- **Windows 11 pins the taskbar to the bottom**, so the top/left/right widget-embedding code is
  reasoning-verified only, not live-tested. Don't claim it's verified.
