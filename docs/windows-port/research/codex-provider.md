# Codex provider protocol spec

> Extracted from upstream steipete/CodexBar by gpt-5.5 research agents, 2026-07-04.

## Key facts

- Auto strategy in current source is OAuth first, then CLI RPC; web dashboard is not in auto resolution despite docs describing optional web extras.
- Codex auth is read from `$CODEX_HOME/auth.json` or `<home>/.codex/auth.json`; on Windows that means `%USERPROFILE%\.codex\auth.json` when `CODEX_HOME` is absent.
- Token refresh is `POST https://auth.openai.com/oauth/token` with client id `app_EMoamEEZ73f0CkXaXp7hrann`, grant type `refresh_token`, and scope `openid profile email`.
- Default usage request is `GET https://chatgpt.com/backend-api/wham/usage` with `Authorization: Bearer <token>`, `User-Agent: CodexBar`, `Accept: application/json`, and optional `ChatGPT-Account-Id`.
- Reset credits request is `GET https://chatgpt.com/backend-api/wham/rate-limit-reset-credits` with `OpenAI-Beta: codex-1`, `originator: Codex Desktop`, and optional `ChatGPT-Account-ID`.
- Usage windows map by duration: 300 minutes is session/5h, 10080 minutes is weekly; misordered primary/secondary fields are corrected.
- OAuth network calls use the shared HTTP client with retry disabled by default; 401/403 are unauthorized, other non-2xx statuses are server errors with body text.
- CLI fallback launches `codex -s read-only -a untrusted app-server`, initializes as client `codexbar` version `0.5.4`, then calls `account/rateLimits/read` and `account/read`.
- The macOS web dashboard path uses AppKit/WebKit/browser cookies/Keychain and is compiled unavailable on non-macOS.

## Windows portability

Portable: OAuth credential discovery, auth.json schema, refresh flow, usage endpoint, reset-credit endpoint, response parsing, rate-window normalization, credit/monthly-limit derivation, and CLI JSON-RPC protocol can be reimplemented directly. Use `%CODEX_HOME%\auth.json` when `CODEX_HOME` is set; otherwise use `%USERPROFILE%\.codex\auth.json` because upstream resolves `<home>/.codex/auth.json`.

Needs replacement: macOS WebKit dashboard scraping, WKWebsiteDataStore account isolation, Safari/Chrome/Firefox macOS cookie paths, and macOS Keychain cookie cache. On Windows, either omit web dashboard initially or implement separate browser-cookie import for Windows browser stores and a Windows credential/cache store.

Credential save: source uses POSIX 0600 staged write plus rename. On Windows, use a private user ACL and atomic replace/write-through equivalent rather than POSIX mode bits.

## Open questions

- The docs say CLI --source auto prioritizes OpenAI web dashboard before CLI, but the current provider descriptor resolves auto to OAuth then CLI for both app and CLI runtime. Treat source code as authoritative unless the CLI wrapper adds another layer outside the inspected files.
- The exact OpenAI dashboard scrape output schema is broader than the Codex provider files; only directly referenced web helpers were inspected, so a Windows web-dashboard clone would need a separate focused scrape-spec extraction.
- The default URL fallback in resolveUsageURL concatenates defaultChatGPTBaseURL + chatGPTUsagePath; because the default base includes a trailing slash and the path begins with slash, the fallback literal would contain a double slash. Normal default normalization avoids that in ordinary execution.

## Full report

# CODEX Provider Protocol Specification

## Scope
This spec covers Codex provider usage-limit fetching as implemented by the Swift source under `Sources/CodexBarCore/Providers/Codex/`, plus directly called shared helpers. It is intended for a from-scratch Windows implementation. Claims are cited with source file paths.

## 1. Data Sources and Fallback Order

### Configurable source modes
Codex supports usage data source modes `auto`, `oauth`, and `cli`; display names are `Auto`, `OAuth API`, and `CLI (RPC/PTY)`. Source labels are `auto`, `oauth`, and `cli`. Source: `Sources/CodexBarCore/Providers/Codex/CodexUsageDataSource.swift`.

The provider descriptor advertises source modes `[.auto, .web, .cli, .oauth]`, but strategy resolution only returns OAuth/CLI for `.auto`, and a single strategy for explicitly selected `.oauth`, `.cli`, or `.web`. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

### App and CLI runtime fallback
For both app runtime and CLI runtime, `auto` resolves to `[CodexOAuthFetchStrategy(), CodexCLIUsageStrategy()]`; `.oauth` resolves to `[oauth]`; `.cli` resolves to `[cli]`; `.web` resolves to `[web]`; `.api` resolves to `[]`. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

OAuth fallback to CLI is allowed only in `auto` mode and only for OAuth/auth states that the CLI can plausibly repair: `CodexOAuthFetchError.unauthorized`, `CodexOAuthCredentialsError.notFound`, `CodexOAuthCredentialsError.missingTokens`, and token refresh errors `.expired`, `.revoked`, `.reused`. OAuth invalid JSON/API/server/network errors do not fall back in source behavior. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

CLI strategy never falls back after an error. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

Web dashboard strategy is macOS-only. In `#else` non-macOS builds it is unavailable, throws `ProviderFetchError.noAvailableStrategy(.codex)`, and never falls back. Source: `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`.

### Data source priority order for Windows implementation
1. OAuth API from Codex CLI auth file, via `~/.codex/auth.json` or `$CODEX_HOME/auth.json`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`, `Sources/CodexBarCore/CodexHomeScope.swift`.
2. Codex CLI RPC probe, launched as `codex -s read-only -a untrusted app-server`, then JSON-RPC methods `initialize`, `account/rateLimits/read`, and `account/read`. Source: `Sources/CodexBarCore/UsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.
3. Web dashboard cookies are an explicit `.web` source or macOS-only strategy; they are not in app/CLI `auto` strategy resolution in current source. The docs describe OpenAI web extras as optional follow-up enrichment, but the concrete descriptor does not include web in `auto`. Source: `docs/codex.md`, `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`, `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`.
4. Local Codex CLI files are used for auth (`auth.json`) and cost/session scanning in docs, not as the remote rate-limit source except through auth-backed OAuth and CLI RPC. Source: `docs/codex.md`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

## 2. Auth and Credential Storage

### Codex home resolution
`CodexHomeScope.ambientHomeURL` returns `$CODEX_HOME` when the environment variable is present and non-empty after trimming; otherwise it returns `<home>/.codex`. Source: `Sources/CodexBarCore/CodexHomeScope.swift`.

The OAuth credential store appends `auth.json` to that Codex home. Therefore upstream reads `$CODEX_HOME/auth.json` or `<home>/.codex/auth.json`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`, `Sources/CodexBarCore/CodexHomeScope.swift`.

On Windows, map `<home>/.codex/auth.json` to `%USERPROFILE%\.codex\auth.json`, while still honoring `CODEX_HOME` exactly if set. This follows the source's home-relative logic, not a macOS-specific path. Source: `Sources/CodexBarCore/CodexHomeScope.swift`.

### `auth.json` schema
The store accepts either API-key auth or OAuth token auth. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

Accepted API-key form:
```json
{
  "OPENAI_API_KEY": "non-empty string"
}
```
If `OPENAI_API_KEY` is a non-empty string after whitespace trimming, the credential loader returns `accessToken = apiKey`, `refreshToken = ""`, `idToken = null`, `accountId = null`, `lastRefresh = null`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

Accepted OAuth form:
```json
{
  "OPENAI_API_KEY": null,
  "tokens": {
    "access_token": "string",
    "refresh_token": "string",
    "id_token": "string optional",
    "account_id": "string optional"
  },
  "last_refresh": "ISO-8601 string optional"
}
```
The parser also accepts camelCase token keys `accessToken`, `refreshToken`, `idToken`, and `accountId`. `access_token/accessToken` must be non-empty and `refresh_token/refreshToken` must exist. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

`last_refresh` is parsed as ISO-8601 with fractional seconds first, then plain internet date-time. Missing or unparsable values produce `lastRefresh = nil`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

### Refresh decision
`needsRefresh` is true when `lastRefresh` is missing or older than `8 * 24 * 60 * 60` seconds. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

`CodexOAuthFetchStrategy.fetch` refreshes only when `credentials.needsRefresh` is true and `refreshToken` is non-empty. It then saves refreshed credentials back to `auth.json`. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

### Token refresh request
Endpoint: `POST https://auth.openai.com/oauth/token`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

Headers:
```http
Content-Type: application/json
```
Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

JSON request body:
```json
{
  "client_id": "app_EMoamEEZ73f0CkXaXp7hrann",
  "grant_type": "refresh_token",
  "refresh_token": "<existing refresh token>",
  "scope": "openid profile email"
}
```
Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

Successful response is expected to be JSON. The refresher reads `access_token`, `refresh_token`, and `id_token`; each field falls back to the existing credential value if absent. `accountId` is preserved from the old credentials. `lastRefresh` becomes current time. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

Refresh failure taxonomy: error code `refresh_token_expired` maps to `.expired`; `refresh_token_reused` maps to `.reused`; `invalid_grant` and `refresh_token_invalidated` map to `.revoked`; HTTP 401 without a known code maps to `.expired`; other non-200 responses map to `.invalidResponse("Status <code>")`; transport/other errors map to `.networkError`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

### Credential save behavior
Saving preserves existing top-level JSON keys when possible, rewrites `tokens` with snake_case keys, updates `last_refresh` to current ISO-8601 time, creates the containing directory, writes a staged file with POSIX mode `0600`, fsyncs, then renames it over the target. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

## 3. OAuth Usage HTTP Protocol

### Base URL resolution
Default base URL is `https://chatgpt.com/backend-api/`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

The fetcher optionally reads `chatgpt_base_url = ...` from `$CODEX_HOME/config.toml` or `<home>/.codex/config.toml`. It strips comments after `#`, trims whitespace, accepts single or double quoted values, and returns the first `chatgpt_base_url` assignment. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Normalization strips trailing slashes. If the base starts with `https://chatgpt.com` or `https://chat.openai.com` and does not contain `/backend-api`, `/backend-api` is appended. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Usage path selection: if normalized base contains `/backend-api`, append `/wham/usage`; otherwise append `/api/codex/usage`. Fallback URL is `https://chatgpt.com/backend-api//wham/usage` by literal concatenation of default base plus path if URL construction fails, though normal resolution produces `https://chatgpt.com/backend-api/wham/usage` after trailing slash normalization. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

### Usage request
Method: `GET`. Default URL: `https://chatgpt.com/backend-api/wham/usage`. Timeout: 30 seconds. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Headers:
```http
Authorization: Bearer <accessToken>
User-Agent: CodexBar
Accept: application/json
ChatGPT-Account-Id: <accountId>   # only when accountId is non-empty
```
Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

There is no request body. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

### Usage response schema
Top-level decoded schema:
```json
{
  "plan_type": "guest|free|go|plus|pro|free_workspace|team|business|education|quorum|k12|enterprise|edu|unknown string optional",
  "rate_limit": {
    "primary_window": {
      "used_percent": 0,
      "reset_at": 1735401600,
      "limit_window_seconds": 18000
    },
    "secondary_window": {
      "used_percent": 0,
      "reset_at": 1735920000,
      "limit_window_seconds": 604800
    },
    "individual_limit": {
      "limit": 0,
      "used": 0,
      "remaining_percent": 100,
      "resets_at": 1735920000
    }
  },
  "credits": {
    "has_credits": true,
    "unlimited": false,
    "balance": 150.0
  },
  "individual_limit": {
    "limit": 0,
    "used": 0,
    "remaining_percent": 100,
    "resets_at": 1735920000
  },
  "additional_rate_limits": [
    {
      "limit_name": "string optional",
      "metered_feature": "string optional",
      "rate_limit": {
        "primary_window": {
          "used_percent": 0,
          "reset_at": 1735401600,
          "limit_window_seconds": 18000
        },
        "secondary_window": {
          "used_percent": 0,
          "reset_at": 1735920000,
          "limit_window_seconds": 604800
        },
        "individual_limit": {
          "limit": 0,
          "used": 0,
          "remaining_percent": 100,
          "resets_at": 1735920000
        }
      }
    }
  ]
}
```
Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Field meanings from source mapping: `plan_type` is account plan/login method; `rate_limit.primary_window` and `secondary_window` become session/weekly lanes after duration-based normalization; `used_percent` is consumed percentage; `reset_at` is Unix epoch seconds; `limit_window_seconds` becomes window duration minutes; `credits.balance` becomes remaining credit balance; `individual_limit` becomes the monthly credit limit; `additional_rate_limits` become named extra windows. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexReconciledState.swift`, `Sources/CodexBarCore/Providers/Codex/CodexRateWindowNormalizer.swift`, `Sources/CodexBarCore/Providers/Codex/CodexSpendControlLimitMapping.swift`, `Sources/CodexBarCore/Providers/Codex/CodexAdditionalRateLimitMapper.swift`.

Plan enum accepts exact known values `guest`, `free`, `go`, `plus`, `pro`, `free_workspace`, `team`, `business`, `education`, `quorum`, `k12`, `enterprise`, and `edu`; unknown strings are preserved. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

`credits.balance` accepts either number or numeric string. Missing booleans default to `false`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

`individual_limit` is accepted both top-level and nested under `rate_limit`; camelCase `individualLimit`, `remainingPercent`, and `resetsAt` are also accepted. `limit`, `used`, `remaining_percent/remainingPercent`, and `resets_at/resetsAt` accept numeric or numeric-string values where implemented. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

### Rate-limit reset credits request
The app fetches reset credits for runtime `.app`; CLI runtime fetches them only when `includeCredits` is true. Failure is swallowed with `try?` and does not fail the main OAuth usage fetch. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

Endpoint path: `/wham/rate-limit-reset-credits` appended to the normalized ChatGPT base. Unlike usage URL selection, this always uses the wham path. Default URL: `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Method: `GET`. Default timeout: 4 seconds. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Headers:
```http
Authorization: Bearer <accessToken>
User-Agent: CodexBar
Accept: application/json
OpenAI-Beta: codex-1
originator: Codex Desktop
ChatGPT-Account-ID: <accountId>   # only when accountId is non-empty; note uppercase ID
```
Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Response schema:
```json
{
  "credits": [
    {
      "id": "string",
      "reset_type": "string",
      "status": "available|redeeming|redeemed|expired|unknown string",
      "granted_at": "ISO-8601 date string",
      "expires_at": "ISO-8601 date string or null",
      "redeem_started_at": "ISO-8601 date string or null",
      "redeemed_at": "ISO-8601 date string or null",
      "title": "string or null",
      "description": "string or null"
    }
  ],
  "available_count": 0
}
```
Dates decode as ISO-8601 with fractional seconds first, then without fractional seconds. `available_count` must be >= 0 or the response is invalid. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`, `Sources/CodexBarCore/CreditsModels.swift`.

### HTTP status handling and retry behavior
For usage and reset-credit requests: HTTP 200...299 decodes JSON; 401/403 throws `.unauthorized`; all other statuses throw `.serverError(statusCode, bodyString)`; decode failures throw `.invalidResponse`; transport errors throw `.networkError`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

The shared HTTP transport default retry policy is disabled. `ProviderHTTPTransport.response(for:)` calls `response(for:retryPolicy:.disabled)`. Therefore Codex OAuth usage, reset credits, and token refresh make one network attempt unless a custom transport or explicit retry policy is introduced. Source: `Sources/CodexBarCore/ProviderHTTPClient.swift`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

The shared retry policy type supports retryable statuses `[408, 429, 500, 502, 503, 504]`, URL errors `[timedOut, networkConnectionLost, cannotConnectToHost, cannotFindHost, dnsLookupFailed]`, methods `[GET, HEAD, OPTIONS]`, base delay 1s, max delay 10s, and `Retry-After`; but Codex OAuth does not opt into it. Source: `Sources/CodexBarCore/ProviderHTTPClient.swift`.

Redirects are allowed only for same-origin HTTPS-to-HTTPS redirects. Source: `Sources/CodexBarCore/ProviderHTTPClient.swift`.

## 4. Parsing and Derivation

### OAuth windows
`CodexReconciledState.fromOAuth` converts `primary_window` and `secondary_window` using `reset_at` as Unix epoch seconds, `limit_window_seconds / 60` as `windowMinutes`, `used_percent` as `Double`, and `UsageFormatter.resetDescription(from:)` for reset description. Source: `Sources/CodexBarCore/Providers/Codex/CodexReconciledState.swift`.

Window normalization is duration-based: 300 minutes is session/5h; 10080 minutes is weekly. If primary is weekly and secondary is session, they are swapped. If only one window exists and it is weekly, it becomes secondary; if only one window exists and it is session or unknown, it becomes primary. Source: `Sources/CodexBarCore/Providers/Codex/CodexRateWindowNormalizer.swift`.

A reconciled usage snapshot is produced only if normalized primary or secondary exists. Extra windows alone do not resurrect a usage snapshot. Source: `Sources/CodexBarCore/Providers/Codex/CodexReconciledState.swift`.

If no rate-limit windows exist but credits or reset credits exist, OAuth returns a partial `UsageSnapshot` with `primary = nil`, `secondary = nil`, `tertiary = nil`, reset credits if any, and OAuth identity. If neither windows nor credits/reset credits exist, it throws `UsageError.noRateLimitsFound`. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

If `rate_limit.primary_window`, `secondary_window`, or `additional_rate_limits` were present but malformed, decode failure is tracked and returned data confidence becomes `.unknown`; otherwise confidence is `.exact`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

### Additional model-specific limits
`additional_rate_limits` is optional and additive. Absent or non-array field leaves `additionalRateLimits = nil`; malformed array elements are ignored individually and mark decode failure. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Spark limits are detected when `limit_name` or `metered_feature` lowercased contains `spark`. Stable IDs/titles are `codex-spark` / `Codex Spark 5-hour` and `codex-spark-weekly` / `Codex Spark Weekly`. Source: `Sources/CodexBarCore/Providers/Codex/CodexAdditionalRateLimitMapper.swift`.

For Spark, both primary and secondary snapshots can become named windows. A window <= 6 hours is classified five-hour; a window >= 6 days is classified weekly; otherwise fallback order treats primary as five-hour and secondary as weekly. Source: `Sources/CodexBarCore/Providers/Codex/CodexAdditionalRateLimitMapper.swift`.

For non-Spark additional limits, primary window is preferred and secondary is used only if primary is absent. ID is `codex-<slug>` from first non-empty `metered_feature` then `limit_name`; title is first non-empty `limit_name` then `metered_feature`, default `Codex extra limit`. Slug lowercases and keeps alphanumerics, replacing other runs with `-`. Source: `Sources/CodexBarCore/Providers/Codex/CodexAdditionalRateLimitMapper.swift`.

Additional named window reset time is nil when `reset_at <= 0`; window minutes is nil when `limit_window_seconds <= 0`; otherwise same conversion as primary windows. Source: `Sources/CodexBarCore/Providers/Codex/CodexAdditionalRateLimitMapper.swift`.

### Credits and monthly limit
OAuth `credits.balance` becomes `CreditsSnapshot.remaining`; events are empty; `updatedAt` is current time. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`, `Sources/CodexBarCore/CreditsModels.swift`.

Monthly credit limit uses first available `individual_limit` from top-level response, then nested `rate_limit.individual_limit`. It is included only when `limit > 0`. If `used` is missing but `remaining_percent` exists, used is derived as `limit * max(0, min(100, 100 - remainingPercent)) / 100`; otherwise used defaults to 0. Remaining percent is provided value or derived as `max(0, min(100, 100 - used / limit * 100))`. `resets_at > 0` becomes a date; otherwise nil. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`, `Sources/CodexBarCore/Providers/Codex/CodexSpendControlLimitMapping.swift`, `Sources/CodexBarCore/CreditsModels.swift`.

If OAuth in auto mode with credits returns `remaining == 0`, no monthly credit limit, and no reset credits, it tries CLI once to obtain a monthly limit. The CLI monthly limit is adopted only when CLI returned `codexCreditLimit`, OAuth and CLI emails are both present and equal case-insensitively, and OAuth credits exist. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

### Identity
OAuth identity email is decoded from JWT `id_token` payload: first top-level `email`, then `https://api.openai.com/profile.email`. Source: `Sources/CodexBarCore/Providers/Codex/CodexReconciledState.swift`, `Sources/CodexBarCore/UsageFetcher.swift`.

OAuth plan/login method is `response.plan_type.rawValue` when present and non-empty; otherwise JWT `https://api.openai.com/auth.chatgpt_plan_type`, then top-level `chatgpt_plan_type`. Source: `Sources/CodexBarCore/Providers/Codex/CodexReconciledState.swift`.

Auth-backed CLI account identity also reads `chatgpt_account_id` from credential `accountId`, JWT auth claim, or top-level JWT claim, plus email/plan claims. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

## 5. CLI RPC Protocol

### Launch command
The CLI strategy resolves the `codex` binary and launches app-server as:
```text
codex -s read-only -a untrusted app-server
```
Source: `Sources/CodexBarCore/UsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

Initialize timeout is 8.0 seconds; normal request timeout is 3.0 seconds. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

### JSON-RPC messages
CodexBar sends newline-delimited JSON over stdin/stdout. Initialize request method is `initialize` with params:
```json
{
  "clientInfo": {
    "name": "codexbar",
    "version": "0.5.4"
  }
}
```
After initialize, it sends notification method `initialized`. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

It then calls `account/rateLimits/read`, then attempts `account/read`. Requests are serialized because app-server answers on one stdout stream. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

### CLI rate-limits response schema
Decoded response result:
```json
{
  "rateLimits": {
    "limitId": "string optional",
    "limitName": "string optional",
    "primary": {
      "usedPercent": 0.0,
      "windowDurationMins": 300,
      "resetsAt": 1735401600
    },
    "secondary": {
      "usedPercent": 0.0,
      "windowDurationMins": 10080,
      "resetsAt": 1735920000
    },
    "credits": {
      "hasCredits": true,
      "unlimited": false,
      "balance": "150.0"
    },
    "individualLimit": {
      "limit": 0,
      "used": 0,
      "remainingPercent": 100,
      "resetsAt": 1735920000
    },
    "planType": "string optional",
    "rateLimitReachedType": "string optional"
  },
  "rateLimitsByLimitId": {
    "id": { "...": "same RPCRateLimitSnapshot schema" }
  }
}
```
Snake_case alternatives accepted for `rate_limits_by_limit_id`, `limit_id`, `limit_name`, `individual_limit`, `plan_type`, `rate_limit_reached_type`, `remaining_percent`, and `resets_at`. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

### CLI account response schema
Decoded account response:
```json
{
  "account": {
    "type": "apikey|chatgpt",
    "email": "string optional for chatgpt",
    "planType": "string optional for chatgpt"
  },
  "requiresOpenaiAuth": true
}
```
`apikey` maps to API-key account. `chatgpt` maps email and plan, defaulting each to `unknown` if absent. Unknown account types fail decode. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

### CLI derivation
CLI windows convert `usedPercent` directly, `windowDurationMins` directly, `resetsAt` as Unix epoch seconds, and reset description with `UsageFormatter.resetDescription`. Then the same `CodexReconciledState.fromCLI` normalization applies. Source: `Sources/CodexBarCore/UsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexReconciledState.swift`, `Sources/CodexBarCore/Providers/Codex/CodexRateWindowNormalizer.swift`.

CLI credits parse `credits.balance` string to `Double`, defaulting to 0 if missing/unparseable, and combine with monthly credit limit candidate. Monthly limit is searched first in root `rateLimits`, then sorted `rateLimitsByLimitId` values. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

If CLI has credits but no usage windows, it returns credits plus an empty identified usage snapshot when plan/email identity exists; otherwise no-rate-limits. Source: `Sources/CodexBarCore/UsageFetcher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`.

If app-server returns an error whose message contains `body=<json>`, the code extracts the JSON object and decodes `email`, `plan_type`, `rate_limit`, and `credits` using the OAuth response window/credit schemas. It can recover usage and credits from that error body. Source: `Sources/CodexBarCore/UsageFetcher.swift`.

## 6. Web Dashboard Cookies and Scrape Path

Web dashboard URL is `https://chatgpt.com/codex/settings/usage`. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`, `docs/codex.md`.

The concrete web strategy is macOS-only and initializes `NSApplication.shared`, uses WebKit through `OpenAIDashboardFetcher`, and returns source label `openai-web`. Source: `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`.

Cookie import domains are `chatgpt.com` and `openai.com`. Source: `Sources/CodexBarCore/OpenAIWeb/OpenAIDashboardBrowserCookieImporter.swift`, `docs/codex.md`.

Docs list automatic browser cookie sources as Safari `~/Library/Cookies/Cookies.binarycookies`, Chrome/Chromium `~/Library/Application Support/Google/Chrome/*/Cookies`, and Firefox `~/Library/Application Support/Firefox/Profiles/*/cookies.sqlite`; no cookie-name filter, all matching domain cookies are imported. Source: `docs/codex.md`.

Cookie cache is documented as macOS Keychain service `com.steipete.codexbar.cache`, account `cookie.codex`, storing source and timestamp. Source: `docs/codex.md`.

Manual cookie mode accepts a pasted `Cookie:` header from a `chatgpt.com` request. The importer normalizes it, splits pairs, creates `HTTPCookie` objects for domain `.chatgpt.com`, path `/`, secure true, and rejects empty/invalid headers. Source: `docs/codex.md`, `Sources/CodexBarCore/OpenAIWeb/OpenAIDashboardBrowserCookieImporter.swift`.

The importer validates imported cookies against `https://chatgpt.com/backend-api/me` and `https://chatgpt.com/api/auth/session` using a `Cookie` header built from chatgpt.com cookies. Source: `Sources/CodexBarCore/OpenAIWeb/OpenAIDashboardBrowserCookieImporter.swift`.

Signed-in email is extracted from dashboard HTML `client-bootstrap` JSON or `__NEXT_DATA__` per docs and scrape script references. Source: `docs/codex.md`, `Sources/CodexBarCore/OpenAIWeb/OpenAIDashboardScrapeScript.swift`.

Web dashboard ownership is fail-closed: dashboard signed-in email must exist, must match expected scoped email when known, and provider-account identities require exact ownership proof. Ambiguous same-email owners produce `displayOnly`; mismatches or missing proof produce `failClosed`. Source: `Sources/CodexBarCore/Providers/Codex/CodexDashboardAuthority.swift`, `Sources/CodexBarCore/Providers/Codex/CodexCLIDashboardAuthorityContext.swift`, `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`.

Web strategy retries once with fresh browser import only after `OpenAIWebCodexError.missingUsage` or `OpenAIDashboardFetcher.FetchError.noDashboardData`; otherwise it does not retry. Source: `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`.

## 7. Error Taxonomy

Credential load errors: `.notFound`, `.decodeFailed(String)`, `.missingTokens`. User-facing strings are `Codex auth.json not found. Run codex to log in.`, `Failed to decode Codex credentials: <message>`, and `Codex auth.json exists but contains no tokens.` Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

OAuth fetch errors: `.unauthorized`, `.invalidResponse`, `.serverError(Int,String?)`, `.networkError(Error)`. User-facing strings distinguish expired/invalid OAuth token, invalid usage response, Codex API status/body, and network error. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`.

Refresh errors: `.expired`, `.revoked`, `.reused`, `.networkError(Error)`, `.invalidResponse(String)`. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`.

General usage errors include `noSessions`, `noRateLimitsFound`, and `decodeFailed` with strings `No Codex sessions found yet. Run at least one Codex prompt first.`, `Found sessions, but no rate limit events yet.`, and `Could not parse Codex session log.` Source: `Sources/CodexBarCore/UsageFetcher.swift`.

Web errors include login required/no dashboard data from OpenAI dashboard fetcher, `OpenAIWebCodexError.missingUsage`, `.policyRejected`, `.timedOut`, and `CodexDashboardPolicyError.displayOnly`. Source: `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`, `Sources/CodexBarCore/Providers/Codex/CodexDashboardAuthority.swift`.

## 8. macOS-Specific vs Portable

Portable to Windows: Codex home resolution via `CODEX_HOME` or user home `.codex`; auth.json schema; token refresh endpoint/body/client id; OAuth usage endpoints; reset-credit endpoint/headers; response schemas; derivation of windows/credits/monthly limits; CLI RPC command and JSON-RPC methods, assuming Windows Codex CLI supports `app-server`. Source: `Sources/CodexBarCore/CodexHomeScope.swift`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexTokenRefresher.swift`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthUsageFetcher.swift`, `Sources/CodexBarCore/UsageFetcher.swift`.

Needs Windows replacement: Web dashboard source uses macOS `AppKit`, `WKWebView`, `WKWebsiteDataStore`, macOS Keychain cache, and macOS browser cookie locations. Current non-macOS source disables the web strategy. Source: `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`, `docs/codex.md`.

Windows path: upstream relative-to-home logic should read `%USERPROFILE%\.codex\auth.json` when `CODEX_HOME` is absent, and `%CODEX_HOME%\auth.json` when set. Source: `Sources/CodexBarCore/CodexHomeScope.swift`, `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

Windows should not port POSIX `open`, `fchmod`, and `rename` implementation literally; use Windows private ACL / atomic replace semantics for saving refreshed credentials. Source: `Sources/CodexBarCore/Providers/Codex/CodexOAuth/CodexOAuthCredentials.swift`.

Windows taskbar app should use the OAuth path first because it is portable and does not depend on macOS WebKit or cookie stores. Source: `Sources/CodexBarCore/Providers/Codex/CodexProviderDescriptor.swift`, `Sources/CodexBarCore/Providers/Codex/CodexWebDashboardStrategy.swift`.

---

Note on tool invocation: two earlier `codex exec` attempts in this session (`-s read-only` and `-s workspace-write`) both failed to read any repository files, self-reporting `windows sandbox: CreateProcessWithLogonW failed: 5` (a Windows sandbox process-creation permission error), and returned an explicit refusal to fabricate the spec rather than any protocol content. A third attempt with `-s danger-full-access` succeeded in reading the actual local source files under `C:\dev\01-active-projects\CodexWinBar` and produced the verbatim, source-cited spec above. All facts, quotes, and file-path citations above come from that successful run reading the real local repository, not from memory/training data.
