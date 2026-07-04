# Claude provider protocol spec

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Key facts

- Both codex exec invocations exited 0 but Codex's internal Windows sandbox could not launch any child process, failing with 'windows sandbox: CreateProcessWithLogonW failed: 5' on every file-read attempt.
- This occurred identically under -s read-only (attempt 1) and -s workspace-write (attempt 2, the permitted retry), indicating an environment/permissions issue rather than a sandbox-mode-specific problem.
- No repository files (docs/claude.md, Sources/CodexBarCore/Providers/Claude/, or any helpers) were ever actually read by Codex in either attempt.
- Codex explicitly declined to infer or fabricate protocol details in the absence of verified source access.

## Windows portability

Not determined — Codex was unable to read any repository source in either attempt due to a sandbox process-launch failure (CreateProcessWithLogonW error 5), so no verified Windows-portability assessment of the Claude provider (auth storage, OAuth refresh, usage endpoints) could be produced this run.

## Open questions

- Why does Codex's Windows sandbox fail with CreateProcessWithLogonW error 5 (access denied) when trying to launch any child process, in both read-only and workspace-write modes, in this environment?
- Is this tied to the current Windows logon session/token (e.g., running under a restricted or non-interactive token) rather than to codex CLI configuration?
- Once the sandbox issue is resolved, what are the exact Claude OAuth endpoints, client_id, scopes, usage-endpoint headers/response schema, and fallback order (credentials file -> Keychain -> CLI probe -> web session) implemented under Sources/CodexBarCore/Providers/Claude/ and docs/claude.md?

## Full report

CODEX-ERROR: Both attempts to run `codex exec` against this task failed to actually inspect the repository. Codex exited with code 0 and produced valid JSON both times, but in every attempt its underlying sandboxed process launch failed before any file could be read, with the identical OS-level error:

`windows sandbox: CreateProcessWithLogonW failed: 5`

This occurred on attempt 1 (`-s read-only`) and again on attempt 2 (`-s workspace-write`, used as the permitted retry), so it is not a transient fluke tied to sandbox mode — it looks like an environment/permissions issue preventing Codex's Windows sandbox from spawning child processes at all (error 5 = ERROR_ACCESS_DENIED from CreateProcessWithLogonW, typically caused by token/logon-session restrictions in the current session).

Verbatim Codex output, attempt 1 (`-s read-only`):
{
  "report": "Blocked: I could not read the repository files. The read-only shell tool failed before process start with `CreateProcessWithLogonW failed: 5` for both the memory lookup and a minimal `Get-Location` command in `C:\\dev\\01-active-projects\\CodexWinBar`. Because this task requires exact endpoint URLs, JSON schemas, constants, and file-path citations from `docs/claude.md`, `Sources/CodexBarCore/Providers/Claude/`, and directly referenced helpers, I cannot produce a reliable protocol specification without filesystem access. I did not infer or fabricate protocol details.",
  "keyFacts": [
    "Filesystem reads were blocked by `CreateProcessWithLogonW failed: 5` before any repository files could be inspected.",
    "No claims about Claude provider endpoints, headers, schemas, constants, or fallback behavior were verified from source files in this turn."
  ],
  "windowsPortability": "Not determined from source due to blocked filesystem access. The user-provided context says the real Windows Claude credentials file shape is `{claudeAiOauth: {accessToken, refreshToken, expiresAt, scopes, subscriptionType, rateLimitTier}}` and implies `%USERPROFILE%\\.claude\\.credentials.json`, but I could not verify implementation details in the repository.",
  "openQuestions": [
    "Why is the read-only shell unable to start PowerShell in this sandbox (`CreateProcessWithLogonW failed: 5`)?",
    "What exact Claude OAuth endpoints, usage endpoints, headers, constants, response schemas, and fallback paths are implemented in the current source files?",
    "Which directly referenced helper files under the allowed limit define portable parsing, HTTP, auth, retry, backoff, and countdown behavior?"
  ]
}

Verbatim Codex output, attempt 2 (`-s workspace-write`, the permitted retry):
{
  "report": "Unable to produce the requested protocol specification because the local read-only file inspection could not be performed in this turn. Every attempted process launch in `C:\\dev\\01-active-projects\\CodexWinBar` failed before reading files, including the scoped command for `docs/claude.md` and `Sources/CodexBarCore/Providers/Claude`, and a minimal `cmd /c dir` probe. The environment returned: `windows sandbox: CreateProcessWithLogonW failed: 5`. No MCP file resources were available. Because the task requires exact file-path citations, verbatim URLs, JSON schemas, constants, and fallback logic from the repository, I cannot safely reconstruct or guess the Claude provider protocol.",
  "keyFacts": [
    "Requested source paths could not be read: docs/claude.md and Sources/CodexBarCore/Providers/Claude.",
    "Shell/process creation failed with: windows sandbox: CreateProcessWithLogonW failed: 5.",
    "No MCP resources were available as an alternate file-read surface.",
    "No protocol details were inferred or fabricated."
  ],
  "windowsPortability": "Not determined from repository sources because the required files could not be read. The user-provided Windows credential shape is known from the prompt only: `%USERPROFILE%\\.claude\\.credentials.json` equivalent of `~/.claude/.credentials.json` may contain `{claudeAiOauth: {accessToken, refreshToken, expiresAt, scopes, subscriptionType, rateLimitTier}}`, but this was not verified against repo code.",
  "openQuestions": [
    "Can the sandbox/process launch issue be resolved so the requested files can be inspected?",
    "What exact OAuth refresh endpoint, client_id, scopes, usage endpoints, headers, response schemas, and fallback order are implemented in the current repository?",
    "Which directly referenced shared helper files define Claude provider constants, backoff behavior, and error taxonomy?"
  ]
}

Per protocol, since retry also failed to accomplish the task (Codex's own sandbox could not launch any process to read files), I am not attempting the analysis myself and am reporting this error verbatim as required.
