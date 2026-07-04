# Provider portability survey

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Key facts

- docs/providers.md: API-token/config providers (OpenRouter apiKey/OPENROUTER_API_KEY, z.ai apiKey/Z_AI_API_KEY) are the cleanest architecture; cookie-source providers cache imported cookies in macOS Keychain under com.steipete.codexbar.cache
- docs/openrouter.md: base https://openrouter.ai/api/v1, credits at /api/v1/credits, key limits/spend at /api/v1/key
- docs/openai.md: Admin usage via GET https://api.openai.com/v1/organization/costs and GET https://api.openai.com/v1/organization/usage/completions, prefers OPENAI_ADMIN_KEY over OPENAI_API_KEY
- docs/copilot.md: GitHub device-flow OAuth via https://github.com/login/device/code and https://github.com/login/oauth/access_token, then GET https://api.github.com/copilot_internal/user with Authorization: token <github_oauth_token>
- docs/gemini.md: reads ~/.gemini/settings.json and ~/.gemini/oauth_creds.json, refresh token via POST https://oauth2.googleapis.com/token, quota via POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota
- docs/cursor.md: relies on browser cookies or stored WebKit session at ~/Library/Application Support/CodexBar/cursor-session.json, endpoints https://cursor.com/api/usage-summary, /api/auth/me, /api/usage?user=ID
- docs/windsurf.md: imports Chromium localStorage LevelDB values (devin_session_token, devin_auth1_token, devin_account_id, devin_primary_org_id), calls POST https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus
- docs/zed.md: macOS-Keychain-bound editor session, GET https://cloud.zed.dev/client/users/me with Authorization: {user_id} {access_token}
- docs/zai.md: GET https://api.z.ai/api/monitor/usage/quota/limit (BigModel host https://open.bigmodel.cn), token via Z_AI_API_KEY
- docs/amp.md: CLI-first (amp usage), API token POST https://ampcode.com/api/internal?userDisplayBalanceInfo, with a browser-cookie fallback path
- docs/kiro.md: pure CLI probe via `kiro-cli chat --no-interactive "/usage"`, no HTTP/OAuth
- docs/ollama.md: API key verified against https://ollama.com/api/tags, but real quota bars require cookie-scraping https://ollama.com/settings
- docs/minimax.md: Coding Plan API token or web cookies against /v1/api/openplatform/coding_plan/remains, token via MINIMAX_CODING_API_KEY
- docs/grok.md: local file ~/.grok/auth.json, `grok agent stdio` JSON-RPC probe, grok.com gRPC-web fallback via Chrome cookies
- docs/antigravity.md: local app/CLI/IDE HTTPS + LSP probes, `agy` PTY interaction, Google OAuth fallback, relies on macOS ps/lsof-style process/port discovery
- First codex exec attempt with -s read-only failed with 'windows sandbox: CreateProcessWithLogonW failed: 5' before any file reads; retry with -s workspace-write succeeded

## Windows portability

Trivial-to-port group (plain HTTPS + key/local JSON, no Keychain/cookies): OpenRouter, OpenAI Admin, z.ai (API-key HTTP only); Copilot core (GitHub device-flow OAuth, token in config); Gemini (OAuth JSON file at ~/.gemini/oauth_creds.json + Google OAuth refresh); Kiro (pure CLI probe); Amp (CLI/API-token path only, excluding its cookie fallback). These need only a Windows-appropriate secret store swap: macOS Keychain -> Windows Credential Manager (CredWriteW/CredReadW) or DPAPI-protected local file (CryptProtectData/CryptUnprotectData, or .NET System.Security.Cryptography.ProtectedData).

Hard-to-port group requiring new seams: Cursor, Windsurf, Factory/Droid, Ollama's quota bars, MiniMax's web-cookie path, and Grok's grok.com fallback all depend on browser cookie/localStorage scraping from Chromium/Safari/WebKit. On Windows this requires reading Chrome/Edge cookie DBs, e.g. `%LOCALAPPDATA%\Google\Chrome\User Data\<Profile>\Network\Cookies` and `%LOCALAPPDATA%\Microsoft\Edge\User Data\<Profile>\Network\Cookies`, decrypted via DPAPI, with additional handling needed for newer Chromium App-Bound Encryption. Zed and Cursor's session store are explicitly macOS-Keychain-bound and need a from-scratch Windows credential-store design. WebKit-stored login/session flows (e.g. Cursor's stored WebKit session) have no direct Windows equivalent and should be replaced by a shared WebView2-based session-export/import seam built once, not duplicated per provider. Antigravity's local process/port discovery (macOS `ps`/`lsof`) needs to become Windows process enumeration (WMI/Toolhelp) plus TCP table lookup (e.g. GetExtendedTcpTable).

Recommended architecture stance for v1: ship the trivial group (Codex, Claude, OpenRouter, OpenAI Admin, Copilot core, Gemini, z.ai, optionally Amp CLI/API-only) and defer building the cookie-import and WebView2-probe seams to v1.5, since those seams are shared infrastructure investments that unlock several providers at once (Cursor, Windsurf, Factory, Ollama quota, MiniMax web, Grok web) rather than one-off effort.

## Open questions

- Whether each provider's Swift implementation has additional Windows-hostile assumptions beyond the docs, especially MiniMax endpoint construction and Antigravity local TLS handling.
- Exact Windows credential locations/behavior for Zed, Cursor, Windsurf, Amp, Grok, and Antigravity CLIs need verification against actual installed Windows apps.
- Whether Copilot's copilot_internal endpoint works with the same device-flow token scopes on Windows without GitHub CLI involvement.
- Whether Gemini CLI on Windows stores oauth_creds.json at the same home-relative path (~/.gemini/) and ships the same OAuth client extraction files.
- Whether Grok's x.ai/billing method is still disabled in current CLI versions; docs reference grok 0.1.210, which may have drifted.
- The first codex exec attempt failed entirely due to a Windows sandbox CreateProcessWithLogonW error under -s read-only; this analysis relies on the -s workspace-write retry, so it may be worth confirming read-only sandbox mode reliability on this Windows machine for future read-only tasks.

## Full report

**Ranking Table**

| Provider | Data Source Type | Windows Portability | Effort | Popularity Value | v1 Recommendation |
|---|---|---:|---:|---|---|
| OpenRouter | Plain API-key HTTPS: `/api/v1/credits`, `/api/v1/key`; `OPENROUTER_API_KEY`; base `https://openrouter.ai/api/v1` | trivial: bearer/key HTTP and config/env only | S | High | ship in v1 |
| OpenAI Admin | Plain API-key HTTPS: `GET https://api.openai.com/v1/organization/costs`, `GET https://api.openai.com/v1/organization/usage/completions`; fallback legacy credit grants | trivial: HTTPS + key, no browser/Keychain | S | High | ship in v1 |
| Copilot | GitHub OAuth device flow; stores token in config; `GET https://api.github.com/copilot_internal/user` | trivial for core quota: device flow + token. Hard only if enabling optional GitHub web-cookie budget extras | M | Very High | ship in v1, defer budget extras |
| Gemini | OAuth file from Gemini CLI: `~/.gemini/oauth_creds.json`, refresh via `https://oauth2.googleapis.com/token`, quota via `cloudcode-pa.googleapis.com` | trivial/medium: local JSON + OAuth refresh. Need Windows Gemini CLI path discovery, but no cookies | M | Very High | ship in v1 |
| z.ai | Plain API-token HTTPS; `GET https://api.z.ai/api/monitor/usage/quota/limit`, BigModel host `https://open.bigmodel.cn`; `Z_AI_API_KEY` | trivial: HTTPS + bearer token + config/env | S | Medium | ship in v1 |
| Amp | CLI first `amp usage`, API token `POST https://ampcode.com/api/internal?userDisplayBalanceInfo`, browser-cookie fallback | trivial for CLI/API token, hard for cookie fallback | M | High | ship in v1 with CLI/API only; cookie fallback v1.5 |
| Kiro | CLI probe only: `kiro-cli chat --no-interactive "/usage"` | trivial if CLI exists on Windows; parser work only | S | Medium | v1.5 |
| Ollama | API key verifies `https://ollama.com/api/tags`; quota bars require cookies scraping `https://ollama.com/settings` | mixed: API-key check trivial, useful quota hard because browser-cookie HTML scrape | M | High | v1.5 if cookie seam exists; otherwise defer |
| MiniMax | Coding Plan API token or web cookies; `/v1/api/openplatform/coding_plan/remains`; `MINIMAX_CODING_API_KEY` | trivial for API-token path, hard for web-cookie path | M | Medium | v1.5 |
| Windsurf | Browser localStorage session bundle from Chromium LevelDB, or local SQLite `state.vscdb`; protobuf `GetPlanStatus` | hard: Chromium localStorage import + token bundle; local SQLite path changes on Windows | L | High | v1.5 |
| Antigravity | Local app/CLI/IDE HTTPS LSP probes, `agy` PTY, Google OAuth fallback | hard: macOS process/`lsof` assumptions need Windows process/port replacement; useful but complex | L | High | v1.5/defer |
| Cursor | Browser cookies, stored WebKit session, local Cursor `state.vscdb`; endpoints `https://cursor.com/api/usage-summary`, `/api/auth/me`, `/api/usage?user=ID` | hard: Safari/WebKit/Keychain paths need Windows Chrome/Edge DPAPI and WebView2 replacement | L | Very High | defer until cookie/WebView2 seam |
| Factory / Droid | Cookies, WorkOS tokens, localStorage LevelDB, session file; Factory + WorkOS APIs | hard: cookie/localStorage import and token minting chain | L | Medium | defer |
| Grok | `~/.grok/auth.json`, `grok agent stdio` JSON-RPC, grok.com gRPC-web via Chrome cookies, local session signals | medium/hard: file + CLI likely portable, billing path currently disabled/fallback cookie-heavy | L | High | defer |
| Zed | macOS Keychain credentials plus `GET https://cloud.zed.dev/client/users/me` | hard: explicitly Keychain-bound; Windows Zed credential storage must be discovered | L | Medium | defer |

**Key Facts**

- Provider registry says API-token/config providers are cleanest: OpenRouter uses `providers[].apiKey` or `OPENROUTER_API_KEY`, and z.ai uses `providers[].apiKey` or `Z_AI_API_KEY`; cookie providers expose a Cookie source picker and some imported cookies are cached in Keychain. Source: `docs/providers.md`.
- OpenRouter fetches credits from `/api/v1/credits` and key limits/spend from `/api/v1/key`, default base `https://openrouter.ai/api/v1`. Source: `docs/openrouter.md`.
- OpenAI Admin uses `GET https://api.openai.com/v1/organization/costs` and `GET https://api.openai.com/v1/organization/usage/completions`; `OPENAI_ADMIN_KEY` is preferred over `OPENAI_API_KEY`. Source: `docs/openai.md`.
- Copilot core quota uses GitHub device flow endpoints `https://github.com/login/device/code`, `https://github.com/login/oauth/access_token`, then `GET https://api.github.com/copilot_internal/user` with `Authorization: token <github_oauth_token>`. Source: `docs/copilot.md`.
- Gemini reads `~/.gemini/settings.json` and `~/.gemini/oauth_creds.json`, refreshes at `POST https://oauth2.googleapis.com/token`, and calls `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota`. Source: `docs/gemini.md`.
- Cursor depends on browser cookies or stored WebKit session at `~/Library/Application Support/CodexBar/cursor-session.json`; cookie cache is macOS Keychain `com.steipete.codexbar.cache`, account `cookie.cursor`. Source: `docs/cursor.md`.
- Windsurf imports Chromium localStorage LevelDB session values `devin_session_token`, `devin_auth1_token`, `devin_account_id`, `devin_primary_org_id`, then calls `POST https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus`. Source: `docs/windsurf.md`.
- Zed reads an editor session from macOS Keychain and calls `GET https://cloud.zed.dev/client/users/me` with `Authorization: {user_id} {access_token}`. Source: `docs/zed.md`.

Note on execution: the first `codex exec` invocation (with `-s read-only`) failed before any repo reads could run, hitting a Windows-specific sandbox error (`windows sandbox: CreateProcessWithLogonW failed: 5`). The retry with `-s workspace-write` succeeded and produced the analysis above.
