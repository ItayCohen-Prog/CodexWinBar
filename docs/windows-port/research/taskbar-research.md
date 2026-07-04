# Windows taskbar embedding research

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Recommendation

Primary technique: top-level overlay window positioned over the taskbar (WS_POPUP with WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, optionally WS_EX_LAYERED), tracked via SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE) on Shell_TrayWnd/Shell_SecondaryTrayWnd plus APPBARDATA/ABM_GETTASKBARPOS, with explicit fullscreen suppression via GetForegroundWindow + MonitorInfo comparison. Fallback / optional "deep integration" mode: SetParent child window reparented into Shell_TrayWnd (TrafficMonitor-style), gated behind runtime compatibility checks and easy user disablement, re-anchored to TrayNotifyWnd's screen rect converted via ScreenToClient.

Reasoning (from Codex/gpt-5.5): SetParent gives the most convincing "always part of the taskbar" look, but it is undocumented, relies on unstable internal child-window hierarchy (Shell_TrayWnd → TrayNotifyWnd, etc.), and is vulnerable to Explorer/taskbar implementation changes across OS updates. A top-level overlay is visually slightly less "native" but is safer, easier to recover cleanly after Explorer restarts (own HWND survives; just re-find taskbar rect and reposition), composes correctly with DWM (Mica/Acrylic/rounded corners work normally, unlike embedded children), and is lower risk for production/compatibility-tool scrutiny. If the product absolutely requires the embedded look, ship SetParent as the default "premium" path after explicit live testing on build 26200, with the overlay kept as an automatic fallback if reparenting/positioning fails or Explorer's structure changes.

Exact call sequence for both paths (embed, position, render, click, flyout, and re-embed-on-TaskbarCreated) is detailed in the report below, including the controller-window pattern for handling TaskbarCreated, WM_DISPLAYCHANGE, WM_THEMECHANGED, WM_SETTINGCHANGE, and WM_DPICHANGED.

## Risks

- DeskBands/taskbar toolbars are deprecated/unavailable through the Win11 taskbar UI and unsuitable for production or Store/MSIX distribution.
- SetParent-into-Shell_TrayWnd relies entirely on undocumented, build-specific internal child-window hierarchy (e.g. TrayNotifyWnd) that Microsoft can change at any OS update, potentially breaking positioning, clipping, or z-order without warning.
- Layered/alpha transparency inside an embedded child window can be unreliable (invalidation, hit-testing, antialiased-text artifacts); DWMWA_SYSTEMBACKDROP_TYPE does not apply meaningfully to embedded children.
- Cross-process SetParent manipulation of Explorer's taskbar window may be flagged by security/compatibility tooling and could be hardened against in future Windows releases.
- A top-level topmost overlay risks floating above fullscreen games/video unless fullscreen detection is implemented carefully (including borderless fullscreen and UWP edge cases), and risks fighting taskbar auto-hide or system flyouts/Quick Settings z-order.
- Taskbar alignment changes (left vs. centered) may not emit a clean documented message, requiring polling/debounce logic rather than pure event-driven updates.
- Left-side anchoring is fragile due to variable Start/Search/Widgets/Copilot layout; the tray/TrayNotifyWnd is the only reasonably stable anchor, and even that is unverified for build 26200.
- Several specific claims about Windows 11 Home build 26200 (25H2) internal child hierarchy, layered-child behavior, and Explorer hardening could not be verified from Codex's training data and require live testing on the target build before shipping.

## Full report

Source: Codex (gpt-5.5, low reasoning effort) via `codex exec -s read-only`, single-threaded, no repo access. Full verbatim analysis below.

---

I cannot fully verify current Windows 11 24H2/25H2 build 26200 behavior from training data alone. The API patterns below are based on known Windows 10/11 shell behavior, TrafficMonitor-style integrations, and documented Win32/DWM APIs. Treat build-specific taskbar child hierarchy, z-order, and clipping behavior as requiring live validation on build 26200.

## 1. DeskBands / Taskbar Toolbars

Classic DeskBands and taskbar toolbars are not a production path for Windows 11.

Historically, taskbar toolbars used shell band infrastructure: `IDeskBand`, `IDockingWindow`, `IObjectWithSite`, `IPersistStream`, `IInputObject`, COM registration under shell band categories, Explorer-hosted in-process COM objects.

On Windows 10, this was already a legacy Explorer extension model. On Windows 11, the redesigned taskbar removed the user-facing "Toolbars" feature and no longer supports adding arbitrary desk bands to the taskbar in the normal way.

Practical status: deprecated shell extension surface; not available through the Windows 11 taskbar UI; not suitable for Store/MSIX-style distribution; requires Explorer-hosted COM, which is fragile and high-risk; does not match the modern Win11 taskbar composition model; not viable for Windows 11 Home 25H2 production targeting. Even if some COM paths still exist internally, relying on them would be worse than either `SetParent` embedding or a top-level overlay.

## 2. Child Window Parented Into The Taskbar (TrafficMonitor-style)

Discovery:
```cpp
HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
HWND secondary = FindWindowW(L"Shell_SecondaryTrayWnd", nullptr);
```
Historically-seen child windows: `TrayNotifyWnd` (notification area/tray), `ReBarWindow32` (older, less relevant on Win11), `MSTaskSwWClass`, `Start`. On Windows 11 the internal child hierarchy has changed across builds — do not hard-code deep child paths unless live inspection proves stable; for build 26200 this cannot be verified from training data.

Window creation:
```cpp
DWORD style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
DWORD exStyle = WS_EX_TRANSPARENT; // optional, only for click-through areas
HWND widget = CreateWindowExW(exStyle, L"MyTaskbarWidgetClass", nullptr, style,
    0, 0, width, height, nullptr, nullptr, hInstance, nullptr);
```
Avoid `WS_EX_LAYERED` on a true child window unless carefully tested — behavior inside Explorer's composed taskbar can be inconsistent (alpha, invalidation, hit testing).

Reparent:
```cpp
SetParent(widget, taskbar);
SetWindowLongPtrW(widget, GWL_STYLE, style);
SetWindowPos(widget, HWND_TOP, x, y, width, height,
    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_FRAMECHANGED);
```
`SetParent` does not fully reconcile style bits — explicitly set `WS_CHILD` and strip `WS_POPUP` and other incompatible top-level styles.

Positioning (right-side, tray-anchored):
```cpp
HWND tray = FindWindowExW(taskbar, nullptr, L"TrayNotifyWnd", nullptr);
RECT trayScreen; GetWindowRect(tray, &trayScreen);
POINT trayLeft = { trayScreen.left, trayScreen.top };
ScreenToClient(taskbar, &trayLeft);
int x = trayLeft.x - widgetW - ScaleForDpi(6);
int y = (tbClient.bottom - widgetH) / 2;
```
Left-side placement is trickier on Win11 due to Start/Search/Widgets/Copilot and task alignment variability; centered taskbar layout means the running-app area is not a reliable anchor — the tray is the most stable anchor on the primary monitor. Reserve a fixed inset from the edge or recompute on alignment change if anchoring left.

Transparency: preferred strategy is to render only text/glyphs with alpha and let the taskbar background show through (Direct2D/DirectWrite or GDI with `SetBkMode(hdc, TRANSPARENT)` / `ID2D1HwndRenderTarget::Clear(D2D1::ColorF(0, 0.0f))`). True alpha requires compatible composition; for a child window, "don't paint background" is usually more reliable than `WS_EX_LAYERED`. Color-key layering (`SetLayeredWindowAttributes(hwnd, RGB(255,0,255), 0, LWA_COLORKEY)`) is old-school and can produce artifacts with antialiased text — generally worse than drawing only foreground content. `DWMWA_SYSTEMBACKDROP_TYPE` is intended for top-level windows, not taskbar-embedded children — do not expect Mica/Acrylic to work meaningfully there.

Input handling: `WM_MOUSEMOVE`, `WM_MOUSELEAVE` (via `TrackMouseEvent` with `TME_LEAVE`), `WM_LBUTTONDOWN/UP`, `WM_CONTEXTMENU`, `WM_SETCURSOR`. Because the widget is a child of Explorer, avoid non-client activation entirely — show the flyout as a separate top-level window from your own process. For click-through regions, return `HTTRANSPARENT` from `WM_NCHITTEST`, or use `WS_EX_TRANSPARENT` (affects sibling paint/input ordering, harder to debug).

Explorer restart recovery:
```cpp
UINT taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
// in wndproc:
if (msg == taskbarCreated) { RefindTaskbar(); RecreateOrReparentWidget(); RepositionWidget(); }
```
Treat all shell HWNDs (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `TrayNotifyWnd`, your parent relationship) as disposable after restart. Also watch `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE` (areas: `ImmersiveColorSet`, `TraySettings`, `UserPreferences`), `WM_THEMECHANGED`, `WM_DPICHANGED`, `WM_DEVICECHANGE`. Taskbar alignment changes may not send a clean documented message — periodic or event-driven repositioning is needed.

Auto-hide interaction:
```cpp
APPBARDATA abd = {}; abd.cbSize = sizeof(abd);
UINT state = SHAppBarMessage(ABM_GETSTATE, &abd);
bool autoHide = (state & ABS_AUTOHIDE) != 0;
bool alwaysOnTop = (state & ABS_ALWAYSONTOP) != 0;
// position:
SHAppBarMessage(ABM_GETTASKBARPOS, &abd); // abd.rc, abd.uEdge (ABE_BOTTOM/TOP/LEFT/RIGHT)
```
Embedded child advantage: when the taskbar hides, the child hides with it automatically. Risk: if Explorer clips, animates, or recreates taskbar children differently, the child may flicker, disappear, or float incorrectly.

Risks: undocumented integration; Explorer can change taskbar hierarchy at any OS update; potential clipping/z-order issues; per-monitor taskbars require per-HWND handling; input/focus can behave differently across builds; child layered transparency can be unreliable; security/compatibility tools may flag Explorer HWND manipulation; Microsoft could harden or restructure the shell in 25H2+. Still, visually this is the closest to "always part of the taskbar."

## 3. Top-Level Overlay Window Over The Taskbar

Avoids reparenting into Explorer entirely.
```cpp
DWORD style = WS_POPUP;
DWORD exStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
// optionally WS_EX_LAYERED, optionally WS_EX_TRANSPARENT for click-through
HWND overlay = CreateWindowExW(exStyle, L"MyTaskbarOverlayClass", nullptr, WS_POPUP,
    x, y, width, height, nullptr, nullptr, hInstance, nullptr);
ShowWindow(overlay, SW_SHOWNOACTIVATE);
SetWindowPos(overlay, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
```

Position tracking: rough rect via `SHAppBarMessage(ABM_GETTASKBARPOS, &abd)`; precise rect via `GetWindowRect` on `Shell_TrayWnd`. Track movement/size via:
```cpp
HWINEVENTHOOK hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
    nullptr, WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
// filter: if (hwnd == taskbar || hwnd == secondaryTaskbar) RepositionOverlay();
```
Other useful events: `EVENT_OBJECT_SHOW`, `EVENT_OBJECT_HIDE`, `EVENT_SYSTEM_FOREGROUND`, `EVENT_SYSTEM_MOVESIZEEND`. Also handle `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `WM_DPICHANGED`, `WM_POWERBROADCAST`.

Fullscreen detection (required since the overlay is topmost and could cover fullscreen video/games):
```cpp
HWND fg = GetForegroundWindow();
HMONITOR mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
MONITORINFO mi = { sizeof(mi) }; GetMonitorInfoW(mon, &mi);
RECT r; GetWindowRect(fg, &r);
bool fullscreen = r.left <= mi.rcMonitor.left && r.top <= mi.rcMonitor.top &&
                  r.right >= mi.rcMonitor.right && r.bottom >= mi.rcMonitor.bottom;
```
Also account for borderless fullscreen and UWP windows.

Pros: does not inject/reparent into Explorer; survives Explorer restarts more cleanly; easier alpha/composition; DWM attributes work properly; easier Fluent flyout/animation; lower risk of crashing/confusing Explorer.
Cons: harder to look truly native; can float above fullscreen content unless carefully hidden; can fight taskbar auto-hide; may appear above system flyouts/tray menus; requires constant position/visibility tracking; can look like an overlay rather than part of the taskbar; z-order around Explorer/Start/Search/Widgets/notifications/Quick Settings is tricky.

## 4. Matching The Taskbar Look

Two surfaces: (1) the taskbar-integrated widget, (2) the flyout. For the integrated widget, best visual match is a transparent widget drawing only text/glyphs — no custom background fill.

Recommended stack: Direct2D for primitives, DirectWrite for text, per-monitor DPI-aware sizing, no custom background fill.
```cpp
SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
UINT dpi = GetDpiForWindow(hwnd);
int px = MulDiv(dp, dpi, 96);
```
Font: `Segoe UI Variable Text` (fallback `Segoe UI`).
```cpp
IDWriteFactory::CreateTextFormat(L"Segoe UI Variable Text", nullptr,
    DWRITE_FONT_WEIGHT_SEMI_LIGHT, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
    fontSize, L"", &format);
```
Practical sizes: 12–13px equivalent at 100% scale; vertically center inside taskbar height; avoid negative letter spacing; use opacity lower than full white in dark mode.

Theme reading (registry): `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` → `AppsUseLightTheme`, `SystemUsesLightTheme`, `EnableTransparency`, read via `RegGetValueW(...)`. Taskbar generally follows `SystemUsesLightTheme` more than app theme. Accent color via `DwmGetColorizationColor(&color, &opaqueBlend)` or registry `HKCU\Software\Microsoft\Windows\DWM` → `ColorizationColor`, `ColorizationColorBalance`. Do not attempt to recreate the taskbar acrylic from these values alone — the shell uses private composition/material logic.

Mica/Acrylic/backdrop (for top-level windows only — flyout, not the embedded child):
```cpp
enum DWM_SYSTEMBACKDROP_TYPE { DWMSBT_AUTO=0, DWMSBT_NONE=1, DWMSBT_MAINWINDOW=2 /*Mica*/,
    DWMSBT_TRANSIENTWINDOW=3 /*Acrylic-like*/, DWMSBT_TABBEDWINDOW=4 };
DWM_SYSTEMBACKDROP_TYPE backdrop = DWMSBT_TRANSIENTWINDOW;
DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(backdrop));
BOOL dark = TRUE;
DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
DWM_WINDOW_CORNER_PREFERENCE pref = DWMWCP_ROUND;
DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &pref, sizeof(pref));
COLORREF border = DWMWA_COLOR_DEFAULT;
DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, &border, sizeof(border));
```
For the embedded taskbar child itself, avoid relying on `DWMWA_SYSTEMBACKDROP_TYPE` — it is not the right abstraction there.

## 5. Flyout

The flyout should be a normal top-level owned popup from your process, not a child of Explorer.
```cpp
DWORD style = WS_POPUP;
DWORD exStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
HWND flyout = CreateWindowExW(exStyle, L"MyFlyoutClass", nullptr, style,
    x, y, width, height, ownerHwnd, nullptr, hInstance, nullptr);
ShowWindow(flyout, SW_SHOWNOACTIVATE);
SetWindowPos(flyout, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
```
Apply DWM dark mode / rounded corners / transient-acrylic backdrop as above. Let DWM provide the shadow; avoid `WS_EX_LAYERED` if you want normal DWM shadow/backdrop behavior.

Positioning:
```cpp
RECT wr; GetWindowRect(widgetOrOverlay, &wr);
int flyoutW = ScaleForDpi(360), flyoutH = ScaleForDpi(280);
// bottom taskbar:
x = wr.right - flyoutW;
y = taskbarRect.top - flyoutH - gap;
// clamp to monitor work area:
HMONITOR mon = MonitorFromRect(&wr, MONITOR_DEFAULTTONEAREST);
MONITORINFO mi = { sizeof(mi) }; GetMonitorInfoW(mon, &mi);
x = std::clamp(x, mi.rcWork.left + margin, mi.rcWork.right - flyoutW - margin);
y = std::clamp(y, mi.rcMonitor.top + margin, mi.rcMonitor.bottom - flyoutH - margin);
```
For top/left/right taskbars, branch on `APPBARDATA.uEdge`.

Light dismiss: because `WS_EX_NOACTIVATE` windows don't activate normally, a low-level mouse hook (`WH_MOUSE_LL`) is often more predictable than relying on `WM_ACTIVATE`/`WM_ACTIVATEAPP`/`WM_KILLFOCUS`/`WM_CAPTURECHANGED`/`WM_KEYDOWN` (VK_ESCAPE)/`WM_NCACTIVATE` alone — test carefully.

## 6. Multi-Monitor And DPI Differences

```cpp
HWND primary = FindWindowW(L"Shell_TrayWnd", nullptr);
HWND h = nullptr;
while ((h = FindWindowExW(nullptr, h, L"Shell_SecondaryTrayWnd", nullptr)) != nullptr) taskbars.push_back(h);
```
Map taskbar → monitor via `MonitorFromRect`. DPI per taskbar/widget: prefer `GetDpiForWindow(hwnd)` for windows you own; `GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &dpiX, &dpiY)` otherwise. Handle `WM_DPICHANGED` using the suggested rect in `lParam`:
```cpp
RECT* suggested = reinterpret_cast<RECT*>(lParam);
SetWindowPos(hwnd, nullptr, suggested->left, suggested->top,
    suggested->right - suggested->left, suggested->bottom - suggested->top,
    SWP_NOZORDER | SWP_NOACTIVATE);
```
Production choices: show only on primary taskbar by default, or optionally on all; maintain one widget HWND per taskbar HWND with independent DPI/font/layout; rebuild the taskbar map after display topology changes (`WM_DISPLAYCHANGE`, `WM_DEVICECHANGE`, `WM_SETTINGCHANGE`, `WM_DPICHANGED`). For overlays, also listen for location changes on every taskbar HWND via `SetWinEventHook`.

## 7. Robustness Matrix

| Scenario | SetParent embedded child | Top-level overlay |
|---|---|---|
| Explorer restart | Must detect TaskbarCreated, discard old HWNDs, reparent/recreate | Must detect, refind taskbar, reposition; own HWND survives |
| Taskbar alignment left/center | Must recompute position relative to tray or known child windows | Same |
| Auto-hide enabled | Usually follows taskbar naturally | Must hide/move with taskbar or it will float incorrectly |
| Fullscreen app | Usually hidden with taskbar | Must explicitly detect and hide |
| DWM/theme changes | Re-render text colors; child backdrop should be transparent | Re-render and update DWM attributes |
| DPI change | Recompute child size and font | Recompute overlay size and position |
| Multi-monitor | One child per Shell_SecondaryTrayWnd; fragile | One overlay per monitor/taskbar; easier |
| OS taskbar internals update | High risk | Medium risk |
| Visual authenticity | Best | Good but imperfect |
| Security/compatibility risk | Higher | Lower |
| Flyout support | Use separate top-level flyout | Natural |
| Z-order correctness | Usually good inside taskbar | Requires active management |
| Click behavior | Feels integrated if done well | Can feel like overlay |
| Production maintainability | Risky | Better |

## Exact Win32 Call Sequence — Embedded SetParent Path

**1. Process init:**
```cpp
SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
UINT taskbarCreatedMsg = RegisterWindowMessageW(L"TaskbarCreated");
RegisterClassExW(&widgetClass); RegisterClassExW(&controllerClass); RegisterClassExW(&flyoutClass);
HWND controller = CreateWindowExW(0, L"MyControllerWindow", nullptr, WS_OVERLAPPED,
    0, 0, 0, 0, nullptr, nullptr, hInstance, nullptr);
```
**2. Find taskbars:**
```cpp
HWND primary = FindWindowW(L"Shell_TrayWnd", nullptr);
HWND secondary = nullptr;
while ((secondary = FindWindowExW(nullptr, secondary, L"Shell_SecondaryTrayWnd", nullptr)) != nullptr) { /* add */ }
```
**3. Create widget per taskbar, reparent:**
```cpp
HWND widget = CreateWindowExW(0, L"MyTaskbarWidget", nullptr,
    WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, 0,0,1,1, nullptr, nullptr, hInstance, nullptr);
SetParent(widget, taskbar);
LONG_PTR style = GetWindowLongPtrW(widget, GWL_STYLE);
style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
SetWindowLongPtrW(widget, GWL_STYLE, style);
LONG_PTR exStyle = GetWindowLongPtrW(widget, GWL_EXSTYLE);
exStyle &= ~(WS_EX_APPWINDOW); exStyle |= WS_EX_TOOLWINDOW;
SetWindowLongPtrW(widget, GWL_EXSTYLE, exStyle);
```
**4. Compute position** (tray-anchored with fallback margin), **5. Render** (WM_PAINT with SetBkMode TRANSPARENT / DirectWrite, theme-aware text color), **6. Handle click** (WM_LBUTTONUP → ShowUsageFlyout), **7. Flyout** (top-level popup owned by controller, DWM dark mode/rounded corners/transient acrylic backdrop), **8. Re-embed on Explorer restart** via controller wndproc handling `taskbarCreatedMsg` (destroy/invalidate widgets, debounce ~250ms or use a timer, rebuild taskbar map) plus `WM_DISPLAYCHANGE`/`WM_THEMECHANGED`/`WM_SETTINGCHANGE` → `SetTimer` debounce → refind taskbars, reposition, invalidate. Do not trust old taskbar HWNDs after TaskbarCreated.

## Exact Win32 Call Sequence — Overlay Fallback Path

1. Create overlay (`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYERED`, `WS_POPUP`, size 1x1 initially); optionally `SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA)`; click-through regions via `WM_NCHITTEST` → `HTTRANSPARENT`.
2. Find taskbar + tray, anchor in screen coordinates, `SetWindowPos(overlay, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW)`.
3. Track taskbar movement via `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, ...)`, filtering for known taskbar HWNDs, posting a custom `WM_APP_REPOSITION` to the controller; also revalidate on `TaskbarCreated`, `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `WM_DPICHANGED`, `EVENT_SYSTEM_FOREGROUND`.
4. Hide during fullscreen: hook `EVENT_SYSTEM_FOREGROUND`, on each foreground change check `IsFullscreenForegroundOnSameMonitor()` → `ShowWindow(overlay, SW_HIDE)` or `SW_SHOWNOACTIVATE` + reposition.

## Build 26200/25H2 Unknowns To Verify Live (explicitly flagged, not confirmed from training data)

- Exact `Shell_TrayWnd` child hierarchy.
- Whether `TrayNotifyWnd` remains a stable direct or indirect child.
- Whether `Shell_SecondaryTrayWnd` behavior is unchanged on all multi-monitor taskbar modes.
- Whether child-window clipping/z-order allows a `SetParent` widget to remain visible near the tray under all taskbar states.
- Whether layered child windows behave cleanly inside the 25H2 taskbar.
- Whether taskbar alignment changes emit useful messages or require polling/debounce.
- Whether Widgets/Copilot/Start/Search regions have changed enough to break left-side anchoring.
- Whether Explorer hardening affects cross-process `SetParent` into taskbar windows.

For production, validate these explicitly on the target build (Windows 11 Home 26200) before committing to the embedded path as the default.

---
Full prompt used (saved for reference): C:\Users\itayc\AppData\Local\Temp\claude\C--dev-01-active-projects-CodexWinBar\d16428d4-4be9-46dc-b125-5352129fee4c\scratchpad\codex-prompt-taskbar-widget.md
Full raw Codex output saved at: C:\Users\itayc\.claude\projects\C--dev-01-active-projects-CodexWinBar\d16428d4-4be9-46dc-b125-5352129fee4c\tool-results\bbppc9f9l.txt
