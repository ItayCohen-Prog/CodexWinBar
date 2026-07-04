# CodexWinBar 🎚️ — May your tokens never run out. Now on Windows.

> Every AI coding limit, on your Windows 11 taskbar.

**CodexWinBar** is a native Windows 11 rebuild of [steipete/CodexBar](https://github.com/steipete/CodexBar)
(the macOS menu-bar app, MIT). It puts your AI coding-provider usage limits **directly on the taskbar** —
embedded next to the system tray so it looks like it was always part of Windows — with per-provider session
and weekly windows, reset countdowns, credits, and live provider-status incidents.

<p align="center">
  <img src="docs/screenshots/widget-embedded.png" alt="CodexWinBar embedded in the Windows 11 taskbar" width="760" />
</p>
<p align="center">
  <img src="docs/screenshots/flyout-light.png" alt="CodexWinBar flyout with provider cards" width="380" />
</p>

## What it does

- **Taskbar-embedded widget** — a compact chip rendered *inside* `Shell_TrayWnd` (true `SetParent`
  embedding with per-pixel alpha, validated by a runtime capability probe) showing tiny session/weekly
  gauges and percent text per provider. If embedding isn't possible on your build, it automatically falls
  back to a tracked overlay; Explorer restarts re-embed automatically.
- **Fluent flyout** — click the widget for provider cards: usage bars, "resets in 2h 13m" countdowns,
  model-specific windows, reset credits, credit balances, plan/account identity, and live status incidents
  (Statuspage/Google Workspace feeds). Acrylic backdrop, rounded corners, light/dark aware.
- **Native engine** — zero-dependency .NET 9: OAuth token refresh, per-provider fetch pipelines with
  fallback strategies, single-flight coalescing, reset-boundary refresh (re-poll ~30s after a window
  resets), quota threshold notifications.
- **Config-compatible with CodexBar** — reads/writes the same `~/.config/codexbar/config.json`
  (`CODEXBAR_CONFIG` and legacy `~/.codexbar` honored; unknown fields and providers round-trip untouched),
  so a dotfile-synced setup works across macOS and Windows.

## Providers (v1)

| Provider | Source | Auth |
|---|---|---|
| **Codex** | ChatGPT backend usage + reset credits | Codex CLI `~/.codex/auth.json` (OAuth, auto-refresh) or API key |
| **Claude** | `api.anthropic.com` OAuth usage | Claude Code `~/.claude/.credentials.json` (auto-refresh) |
| **Copilot** | `copilot_internal/user` quota snapshots | GitHub device flow (built into Settings) |
| **Gemini** | Cloud Code quota API | Gemini CLI `~/.gemini/oauth_creds.json` |
| **OpenRouter** | credits + key limits | API key |
| **OpenAI Admin** | org cost dashboards | Admin API key |
| **z.ai** | coding-plan quota | API key |

Codex and Claude are enabled by default and work with zero setup if their CLIs are signed in. The rest are
enabled in **Settings → Providers**. Upstream's 50+ provider catalog is on the roadmap — the browser-cookie
and WebView2 seams (Cursor, Windsurf, Ollama quota, …) are deliberately deferred to v1.5.

## Install / build

Requires Windows 11 and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (to build).

```powershell
git clone https://github.com/ItayCohen-Prog/CodexWinBar.git
cd CodexWinBar
dotnet publish src/CodexWinBar.App/CodexWinBar.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true -o artifacts/publish
artifacts\publish\CodexWinBar.exe
```

Right-click the widget (or the tray icon) for Refresh / Settings / Quit. "Launch at login" lives in
Settings → General. Logs: `%LOCALAPPDATA%\CodexWinBar\logs\app.log`.

## Architecture

C# / .NET 9, **zero external NuGet dependencies** (test projects excepted):

- `CodexWinBar.Core` — provider abstraction (descriptor + ordered fetch strategies), CodexBar-compatible
  config store, refresh scheduler, status poller.
- `CodexWinBar.Providers` — one folder per provider; contributions need only a descriptor + strategy + parser.
- `CodexWinBar.Widget` — pure Win32: taskbar interop, embed/overlay state machine on a dedicated STA
  thread, GDI+ → `UpdateLayeredWindow` renderer.
- `CodexWinBar.App` — WPF shell: flyout, settings, tray icon, quota notifications, single instance.

Design docs and the upstream protocol research live in [`docs/windows-port/`](docs/windows-port/).

## Credits & license

This is a fork/rebuild of [CodexBar](https://github.com/steipete/CodexBar) by
[Peter Steinberger](https://github.com/steipete) — all product concepts, provider protocol research,
and the original macOS implementation are his. The Windows port reuses upstream's provider documentation
(`docs/`) as its protocol source of truth.

[MIT](LICENSE) — same as upstream.
