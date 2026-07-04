# Windows stack research

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Recommendation

Use C# .NET 9 with WPF for the Fluent flyout/settings UI and a custom Win32 overlay window for the taskbar widget, packaged with Velopack and shipped through GitHub Releases plus winget. This is the strongest balance of native Windows feel, low-enough footprint, fast v1 delivery, async provider ergonomics, and OSS contributor accessibility.

## Risks

- WPF is not NativeAOT-compatible, so the app will not have a tiny native binary; use ReadyToRun, lazy loading, and careful publishing instead.
- The <50 MB working-set target is achievable only with discipline and measurement; realistic WPF builds may sit closer to 50-80 MB.
- True taskbar embedding is fragile on Windows 11; use a tracked overlay window rather than Explorer injection or private taskbar internals.
- WPF needs deliberate styling and DWM interop to feel fully Windows 11-native; it will not look Fluent by default.
- Unsigned GitHub Releases will trigger SmartScreen reputation friction until the project gains signing/reputation.
- Toast activation, launch-at-login, DPI, multi-monitor taskbar positioning, and Explorer restart handling need focused Win32 interop tests.

## Full report

> Note: This report was produced by Codex (gpt-5.5) as a pure knowledge/analysis task; no repository files were read.

# CodexWinBar Tech Stack Evaluation

## Executive Summary

For a production-quality Windows 11 rebuild of macOS CodexBar, the best stack is **C# .NET 9 with a hybrid WPF + Win32 interop architecture**:

- **WPF** for the Fluent-style flyout, settings, tray integration, and contributor-friendly app shell.
- **A tiny custom Win32 layered/tool window** for the always-on taskbar-adjacent widget, rendered with Direct2D/DirectWrite or a lightweight WPF-hosted visual if acceptable.
- **.NET 9 HTTP/JSON/provider engine** with typed provider plugins, async pollers, OAuth refresh, and testable abstractions.
- **Velopack + GitHub Releases + winget** for unsigned-friendly distribution and updates.

This gives the strongest balance of native Windows feel, low footprint, fast v1 delivery, contributor accessibility, and maintainability.

NativeAOT is not viable for WPF. That is acceptable: use **self-contained single-file, trimming where safe, ReadyToRun, and profile-guided startup cleanup** instead. A realistic target is roughly **70-120 MB packaged**, **45-80 MB working set**, and **300-800 ms warm startup**, depending on WPF library choices and whether the widget is kept out of the heavy visual tree.

The strict `<50 MB` RAM target is possible only with discipline and measurement; it should be treated as an optimization target, not a guaranteed baseline for a polished WPF app.

## Requirements Fit

CodexWinBar needs:

- Tiny always-visible taskbar-adjacent widget rendering text and micro-gauges.
- Fluent Windows 11 flyout with provider tiles, progress bars, countdowns.
- Settings window.
- Background HTTP/JSON provider engine with OAuth refresh and around 10 concurrent pollers.
- Tray fallback.
- Toasts.
- Launch at login.
- Single instance.
- Native Windows 11 look: Mica/acrylic, Segoe UI Variable, rounded corners, light/dark.
- No WebView shell.
- Low RAM and instant startup.
- Easy contributor extensibility for 50+ providers.
- GitHub Actions CI.
- GitHub Releases + winget distribution without Microsoft Store dependency.

The hard part is not HTTP or settings. The hard part is the **taskbar-adjacent always-on widget**. True taskbar embedding is fragile on modern Windows because Explorer owns the taskbar surface, and Windows 11 has changed taskbar internals repeatedly. The production-safe approach is a **separate topmost, click-through or tool-window overlay** that tracks taskbar bounds, DPI, monitor changes, auto-hide state, and Explorer restarts.

That favors a stack with strong Win32 interop and good UI productivity.

# 1. C# .NET 9 Options

## 1a. WPF + WPF-UI / ModernWpf Fluent Libraries

### Native Feel

WPF is mature, stable, and still one of the most practical desktop stacks for production Windows utilities. It is not the newest Windows UI framework, but with careful styling it can feel very native on Windows 11:

- Segoe UI Variable can be used explicitly.
- Rounded corners can be handled through window styles and DWM attributes.
- Mica can be applied through Win32/DWM interop on Windows 11.
- Dark/light theme can follow system settings.
- Acrylic-like effects are possible but should be used sparingly for reliability and performance.
- WPF-UI and ModernWpf can provide Fluent controls, navigation, cards, progress bars, toggles, and settings surfaces.

The main caveat: WPF does not automatically look like Windows 11. You need deliberate styling and DWM interop. But for a small menu-bar-style utility, this is manageable.

### Binary Size

Realistic .NET 9 WPF packaging numbers:

- Framework-dependent app: roughly **5-20 MB app payload**, but requires installed .NET Desktop Runtime.
- Self-contained win-x64: roughly **90-150 MB** uncompressed depending on libraries.
- Self-contained single-file: roughly **70-120 MB** compressed-ish on disk after bundling choices.
- ReadyToRun increases size but improves startup.
- Trimming is limited for WPF and reflection-heavy libraries; aggressive trimming is risky.

For winget/GitHub Releases, self-contained is usually preferable unless you want to require the .NET Desktop Runtime.

### Working Set

Realistic working set:

- Minimal WPF tray/background app: **35-60 MB**.
- WPF with Fluent library, settings window, provider engine: **50-90 MB**.
- If the settings window is not created until opened and provider polling is efficient: **45-70 MB** is realistic.
- `<50 MB` is possible but not guaranteed once Fluent libraries and JSON/OAuth code are loaded.

### Startup

Realistic startup:

- Framework-dependent, warm machine: **200-500 ms** to tray/widget.
- Self-contained ReadyToRun: **300-800 ms**.
- Cold start: **800 ms-2 s** depending on disk, AV, and first JIT/load.

Startup can be made to feel instant by showing the widget/tray first, deferring provider initialization, OAuth refresh, settings loading, and image/icon resource loading.

### NativeAOT Viability

**WPF is not NativeAOT-able.**

Use instead:

- ReadyToRun publishing.
- Conservative trimming only if verified.
- Single-file self-contained publishing.
- Lazy window creation.
- Source-generated JSON serializers.
- Avoid reflection-heavy plugin discovery at startup.

Expected result:

- No tiny 5-15 MB native binary.
- But acceptable performance and distribution for a Windows utility.

### Async HTTP/JSON Ergonomics

Excellent.

- `HttpClientFactory` or a small custom `HttpClient` pool.
- `async`/`await` is mature.
- `System.Text.Json` source generation is fast and trim-friendly.
- OAuth refresh flows are straightforward.
- Cancellation tokens, timeouts, retry policies, and background services are easy.
- Testing provider clients is simple with fake `HttpMessageHandler` or interface seams.

For 50+ providers, C# is the most contributor-friendly option among the serious native candidates.

### Interop Burden

Good, but still real.

WPF can call Win32 APIs via P/Invoke for:

- `SetWindowPos`, `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, `WS_EX_LAYERED`, `WS_EX_TRANSPARENT`.
- DWM attributes for Mica, dark mode, rounded corners.
- `SetWinEventHook` for taskbar/Explorer changes.
- `Shell_NotifyIcon` or library wrapper for tray icon.
- Toast activation via Windows App SDK or DesktopToasts-style COM activator.
- Launch at login via registry Run key, StartupTask where packaged, or scheduled task if needed.
- Single instance via named mutex + IPC handoff.

The widget overlay needs careful native handling regardless of UI stack.

### Testing

Strong.

- Provider engine: normal unit tests.
- JSON parsing: snapshot/golden tests.
- OAuth/token state: fake clock + fake HTTP.
- UI models: testable view models or state models.
- Win32 widget positioning: unit-test geometry calculations; reserve manual/VM tests for actual Explorer behavior.
- GitHub Actions Windows runners handle .NET test/build well.

### Verdict

Best practical base for v1. WPF gives mature desktop productivity and strong contributor accessibility. Pair it with a custom Win32 widget layer to avoid forcing WPF into the most fragile part of the app.

## 1b. WinUI 3 / Windows App SDK

### Native Feel

WinUI 3 gives the most official modern Windows 11 control set:

- Fluent controls are native to the framework.
- Mica/Acrylic support is first-class relative to WPF.
- Theme integration is good.
- Typography and spacing are modern by default.

For the flyout and settings window, WinUI 3 looks more Windows 11 out of the box than WPF.

### Binary Size

Realistic unpackaged Windows App SDK app:

- App payload can be modest, but Windows App SDK runtime dependency complicates distribution.
- Self-contained Windows App SDK deployments can become large, often **100-200+ MB** depending on packaging/runtime choices.
- MSIX packaging can be clean, but the requirement says GitHub Releases + winget without Store dependency.

### Working Set

Realistic working set:

- Simple WinUI 3 app: often **80-150 MB**.
- Background utility with windows, tray, and provider engine: **100-180 MB** is not surprising.

This is materially worse than the target and worse than WPF for a tiny always-on utility.

### Startup

Realistic startup:

- Often **700 ms-2 s** warm depending on packaging and machine state.
- Cold start can be slower than WPF.

For a small menu-bar-style background app, this is not ideal.

### NativeAOT Viability

WinUI 3 is not a good NativeAOT target for this kind of desktop app. Windows App SDK + XAML reflection/source generation/runtime dependencies make NativeAOT either unsupported or impractical for normal production use.

### Tool Window / Overlay Quirks

This is a major issue.

WinUI 3 is not as convenient as WPF/Win32 for small utility windows, non-activating overlays, taskbar-adjacent windows, and tray-first apps. It can be done, but you spend more time fighting app/window lifecycle, `AppWindow`, `HWND` interop, packaging assumptions, and desktop utility edge cases.

The always-on widget still requires raw Win32 work. WinUI 3 does not remove that burden.

### Unpackaged Support

Unpackaged WinUI 3 is possible, but there are more deployment and runtime concerns than plain WPF. Toasts, activation, app identity, and updater behavior are often smoother with packaged/MSIX apps, but the desired distribution model is GitHub Releases + winget.

### Async HTTP/JSON Ergonomics

Same as C# WPF: excellent.

### Testing and CI

Provider testing is excellent. UI testing is more cumbersome than WPF view-model testing, and Windows App SDK runtime availability can add CI friction.

### Verdict

Best visual fidelity on paper, but too much runtime overhead and lifecycle/deployment friction for this specific app. I would not choose WinUI 3 for a tiny always-on taskbar utility where footprint, startup, and Win32 control matter.

## 1c. Win32 Interop + Custom-Rendered Widget Window + WPF Flyout/Settings

This is the strongest C# option.

### Architecture

Use two UI layers:

1. **WidgetHost**: custom native `HWND` overlay near or over the taskbar.
   - `WS_EX_TOOLWINDOW` to avoid Alt-Tab.
   - `WS_EX_NOACTIVATE` so it does not steal focus.
   - Optional `WS_EX_TRANSPARENT` for click-through regions.
   - Topmost/non-topmost behavior tuned against taskbar.
   - Tracks taskbar bounds via `SHAppBarMessage`, monitor info, DPI APIs, and Explorer events.
   - Renders text/micro-gauges with Direct2D/DirectWrite or a tiny retained bitmap generated from .NET drawing code.

2. **AppShell**: WPF windows for flyout and settings.
   - Fluent-styled provider tiles.
   - Progress bars/countdowns.
   - Settings and provider management.
   - Tray icon fallback.

### Why This Is Better Than Pure WPF

The widget is the most performance-sensitive and Explorer-sensitive part. A full WPF transparent always-on overlay can work, but it risks:

- Higher memory.
- Layered-window composition quirks.
- DPI weirdness.
- Input/focus edge cases.
- More overhead for a tiny visual updated every few seconds.

A custom Win32 widget keeps the always-running surface small and predictable, while WPF handles the larger UI only when opened.

### Realistic Numbers

With discipline:

- Packaged size: **80-130 MB** self-contained single-file/R2R.
- Working set after startup with widget + tray only: **40-65 MB**.
- Working set after opening flyout/settings: **60-100 MB**.
- Startup to tray/widget: **300-800 ms warm**, with deferred provider initialization.

A framework-dependent build can reduce app download size significantly, but requiring .NET Desktop Runtime hurts consumer distribution.

### NativeAOT

Still no for WPF. The provider engine or small helper processes could theoretically be NativeAOT, but that adds complexity and is not worth it for v1.

### Interop Burden

This option accepts the interop burden explicitly instead of pretending the UI framework will solve it.

Required interop:

- Taskbar geometry and monitor DPI.
- DWM attributes.
- Explorer restart detection.
- WinEvent hooks.
- Tray icon behavior.
- Non-activating flyout positioning.
- Toasts.
- Launch at login.
- Single-instance handoff.

C# can handle this with clean wrappers and tests around geometry/state logic.

### Verdict

This is the recommended stack. It is the best compromise between native behavior, low footprint, fast shipping, and contributor accessibility.

# 2. C++ Win32 + Direct2D

## Native Feel

Excellent if implemented by experienced Windows developers.

- Direct2D/DirectWrite can render beautiful, crisp, low-overhead widgets.
- DWM integration is first-class.
- Tiny memory footprint is achievable.
- Startup can be extremely fast.

Realistic numbers:

- Binary size: **2-15 MB** depending on static/dynamic runtime and dependencies.
- Working set: **10-35 MB** for a careful utility.
- Startup: **50-250 ms** warm.

For the widget itself, this is technically the best runtime choice.

## Developer Velocity

The cost is high.

You would need to build or integrate:

- Fluent-style controls or use WinUI/Windows App SDK anyway.
- Settings UI.
- Provider tiles and progress layouts.
- JSON/OAuth abstractions.
- Async HTTP concurrency.
- Secure token storage.
- Test harnesses.
- Contributor-facing provider APIs.

C++ has good libraries, but the contributor experience for 50+ providers is materially worse than C#:

- More boilerplate.
- More memory/lifetime complexity.
- Harder async ergonomics.
- Harder onboarding.
- More build friction for casual OSS contributors.

## Async HTTP/JSON

Possible, not pleasant compared with .NET.

Options include:

- WinHTTP / WinINet directly.
- C++ REST SDK, Boost.Beast, libcurl.
- nlohmann/json or simdjson.
- Custom coroutine wrappers.

All are workable, but not ideal for dozens of provider integrations maintained by outside contributors.

## Interop

Best possible interop because you are native Win32 from the start.

## Testing and CI

GitHub Actions can build MSVC/CMake projects fine, but test ergonomics and contributor setup are worse than .NET.

## Verdict

Best footprint, worst product velocity. I would use C++ only for a tiny helper DLL/exe if profiling proves the C# widget cannot meet requirements. I would not build the full app in C++ for an extensible provider ecosystem.

# 3. Rust: windows-rs + slint / egui / native-windows-gui

## Runtime and Footprint

Rust can produce small, fast native binaries:

- Binary size: **5-25 MB** depending on GUI stack and static linking.
- Working set: **20-60 MB** depending on UI framework.
- Startup: **50-400 ms** warm.

The core provider engine in Rust would be efficient and reliable.

## windows-rs

`windows-rs` gives strong access to Win32, DWM, COM, notifications, registry, and shell APIs. For the widget overlay and taskbar tracking, Rust is capable.

But the UI ecosystem is the weak point.

## slint

Slint is productive and lightweight compared with web shells. It can produce attractive UIs, but it does not naturally look and feel like a first-party Windows 11 Fluent app.

- Good for custom product UI.
- Less good for "100% native Windows 11".
- Fluent controls, Mica, acrylic, menus, settings idioms, and accessibility need extra work.

## egui

egui is excellent for immediate-mode tools, internal utilities, and cross-platform panels. It is not a native Windows 11 Fluent UI framework.

- Fast to build developer tools.
- Visibly non-native unless heavily customized.
- Not the right choice for a polished Windows 11 consumer utility.

## native-windows-gui

Closer to native controls, but the ecosystem and polish are not where I would want them for a production Fluent Windows 11 app in 2026.

## Async HTTP/JSON

Strong technically:

- `tokio`, `reqwest`, `serde`, `oauth2`, etc.
- Excellent performance.
- Strong type safety.

But contributor accessibility is lower than C# for this app's target ecosystem. More users can contribute a C# provider than a Rust provider.

## Interop Burden

High but manageable. You need to be comfortable with unsafe Windows API boundaries, COM details, and message loops. The UI framework may not abstract enough of the desktop app lifecycle.

## Testing and CI

Rust CI on GitHub Actions is excellent. Provider tests would be strong. UI testing is the weaker area.

## Verdict

Rust is attractive for the engine or a future native helper, but not the best full-app stack when the requirement is "100% native Windows 11 Fluent" plus broad contributor extensibility. The GUI ecosystem is the blocker.

# 4. Electron / Tauri

## Electron

Disqualified.

- Working set commonly **150-300+ MB** for a small app.
- Startup is not instant compared with native options.
- Native Windows 11 feel requires imitation.
- WebView shell explicitly violates the requirement.

## Tauri

Also disqualified for this app despite being lighter than Electron.

- It is still a WebView-based UI shell.
- Native Windows 11 Fluent fidelity is not first-class.
- Taskbar overlay and tray/native integration still require platform-specific code.
- RAM is better than Electron but still not the right answer for "no WebView shell" and "100% native".

Tauri could be reasonable for many cross-platform desktop apps. It is not appropriate here.

# Cross-Cutting Concerns

## Provider Engine

C#/.NET is the best fit:

- `IProviderClient` interface per provider.
- Shared OAuth/token abstractions.
- Source-generated `System.Text.Json` DTOs.
- `HttpClient` with per-provider rate limits and cancellation.
- Background scheduler with jitter and backoff.
- Strong testability with fake clock and fake HTTP.

Avoid dynamic plugin loading for v1. Use source-level provider modules compiled into the app. That keeps trimming, security, CI, and contributor review simpler.

## 10 Concurrent Pollers

.NET handles this comfortably:

- One hosted scheduler service.
- Per-provider polling policy.
- `PeriodicTimer` or channel-based scheduler.
- `SemaphoreSlim` for concurrency caps.
- Strict cancellation on shutdown.
- No thread-per-provider design.

The memory impact of 10 async pollers is tiny compared with the UI framework.

## Toasts

Options:

- Desktop toast COM activator for unpackaged apps.
- Windows App SDK notifications if identity/runtime choices are acceptable.
- BurntToast-style approach is not ideal for app internals but useful as reference.

For v1, use a proven desktop toast library or a small native wrapper. Keep notification activation optional at first if needed.

## Launch at Login

For unpackaged GitHub/winget distribution:

- Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` is simple and common.
- Scheduled task is more controllable but heavier.
- MSIX StartupTask is cleaner only if packaged as MSIX.

Use registry Run for v1, with clear settings UI.

## Single Instance

Use:

- Named mutex.
- Named pipe or local RPC to forward "show settings/flyout" command to existing instance.

This is straightforward in .NET.

## Auto-Update: Velopack vs MSIX

### Velopack

Recommended.

- Works well with GitHub Releases.
- Good fit for unsigned or later-signed desktop apps.
- Easier than MSIX for OSS release flow.
- Supports delta updates and app relaunch patterns.
- Does not require Store account.

### MSIX

Good Windows-native packaging technology, but less ideal here:

- More identity and signing complexity.
- Some APIs get easier when packaged, but distribution and updates become more constrained.
- Winget can install MSIX, but GitHub Releases + auto-update is usually simpler with Velopack.

Use MSIX later only if a specific enterprise/store scenario requires it.

## Unsigned Distribution

Unsigned Windows apps face SmartScreen reputation warnings. This is unavoidable with GitHub Releases regardless of stack.

Practical path:

1. Ship unsigned early builds for contributors/testers.
2. Publish winget manifest once release artifacts stabilize.
3. Add code signing later when the project has users.
4. Keep checksums and release provenance clear.

## GitHub Actions CI

Best path with .NET:

- `windows-latest` runner.
- `dotnet restore`.
- `dotnet build -c Release`.
- `dotnet test -c Release`.
- `dotnet publish -c Release -r win-x64 --self-contained true`.
- Package with Velopack.
- Upload GitHub Release artifacts.
- Generate winget manifest manually or with release automation.

For UI tests, keep most logic out of live windows. Use geometry/state-model tests instead of brittle Explorer automation.

# Recommended Architecture

## Stack

- Language/runtime: **C# .NET 9**.
- App UI: **WPF** with a restrained Fluent Windows 11 style layer, likely WPF-UI or carefully selected ModernWpf pieces.
- Widget: **custom Win32 `HWND` overlay**, rendered via Direct2D/DirectWrite interop or minimal custom drawing; controlled from .NET.
- Provider engine: **.NET async services** with `System.Text.Json` source generation.
- Packaging/update: **Velopack** for GitHub Releases; winget manifest for distribution.
- CI: **GitHub Actions on Windows**.

## Concrete Solution Layout

```text
CodexWinBar.sln

src/
  CodexWinBar.App/
    App.xaml
    App.xaml.cs
    Program.cs
    SingleInstance/
    Tray/
    Toasts/
    Startup/
    Windows/
      FlyoutWindow.xaml
      SettingsWindow.xaml
    Themes/
      Fluent.xaml
      Colors.xaml
      Typography.xaml

  CodexWinBar.Widget/
    WidgetHost.cs
    WidgetWindow.cs
    WidgetRenderer.cs
    TaskbarTracker.cs
    DwmInterop.cs
    Win32Interop.cs
    DpiHelper.cs
    ExplorerEventHook.cs

  CodexWinBar.Core/
    Providers/
      IProvider.cs
      IProviderClient.cs
      ProviderDescriptor.cs
      ProviderSnapshot.cs
      ProviderStatus.cs
    Polling/
      ProviderScheduler.cs
      PollingPolicy.cs
      BackoffPolicy.cs
    Auth/
      OAuthTokenStore.cs
      OAuthRefreshService.cs
      TokenEnvelope.cs
    Settings/
      AppSettings.cs
      SettingsStore.cs
    Time/
      IClock.cs
    Diagnostics/
      AppLogger.cs

  CodexWinBar.Providers/
    OpenAI/
      OpenAIProvider.cs
      OpenAIDtos.cs
      OpenAIJsonContext.cs
    Anthropic/
    GitHub/
    ExampleProvider/

  CodexWinBar.Infrastructure/
    Http/
      ProviderHttpClient.cs
      RateLimitHandler.cs
      RetryHandler.cs
    Json/
    SecureStorage/
      WindowsCredentialStore.cs

  CodexWinBar.Packaging/
    velopack config/scripts

 tests/
  CodexWinBar.Core.Tests/
  CodexWinBar.Providers.Tests/
  CodexWinBar.Widget.Tests/
  CodexWinBar.App.Tests/
```

## Provider Contribution Model

Each provider should be easy to add without understanding the widget or WPF app:

```csharp
public interface IProvider
{
    ProviderDescriptor Descriptor { get; }
    Task<ProviderSnapshot> PollAsync(ProviderContext context, CancellationToken cancellationToken);
}
```

Provider modules should include:

- DTOs.
- Source-generated JSON context.
- Parser tests.
- Fake HTTP tests.
- A descriptor with display name, icon key, auth type, and polling defaults.

Avoid runtime plugin loading in v1. Source-level provider contributions are simpler, safer, and easier to review.

## Widget Strategy

Do not truly inject into Explorer or depend on private taskbar internals.

Use an owned overlay window that:

- Tracks the taskbar rectangle.
- Supports horizontal/vertical taskbars where possible.
- Handles multi-monitor DPI.
- Handles Explorer restart.
- Has conservative fallback placement if taskbar detection fails.
- Can switch to tray-only mode.

This is much more shippable than hard taskbar embedding.

# Decisive Recommendation

Choose **C# .NET 9 with WPF for flyout/settings plus a custom Win32-rendered widget overlay**.

This stack is the best production compromise: it gives a native Windows desktop app without WebView, strong async HTTP/JSON ergonomics, good GitHub Actions support, manageable unsigned GitHub/winget distribution, and the best contributor story for 50+ providers. WinUI 3 looks more modern by default but is too heavy and awkward for a tiny background utility. C++ and Rust can beat C# on footprint, but they lose too much velocity and contributor accessibility for this app.

The v1 should optimize for a disciplined WPF shell, lazy-loaded windows, source-generated JSON, a small Win32 widget host, and Velopack-based releases.
