---
name: verify
description: Verify the static CodexWinBar landing page through its rendered browser surface.
---

# Verify the CodexWinBar landing page

Use this for changes under `docs/`. Do not enable Pages, publish, commit, or push.

1. Establish scope with `git log --oneline @{u}..` and `git diff HEAD --stat`.
2. Serve `docs/` from an isolated local server that maps it to `http://127.0.0.1:<port>/CodexWinBar/` so document-relative project-page paths are exercised.
3. Drive the rendered page in Chromium at 1440, 768, 390, and 320 CSS px in light and dark modes. Capture screenshots and confirm no horizontal overflow, failed assets, console errors, or sticky-header anchor overlap.
4. Keyboard through the skip link, header, hero actions, and install controls. Activate Copy with the keyboard and confirm the exact PowerShell command and live-region response.
5. Probe with JavaScript disabled and with reduced motion. Essential content and the selectable install command must remain visible; only the Copy enhancement should disappear.
6. Run Lighthouse mobile and desktop against the local `/CodexWinBar/` URL. Require Accessibility, Best Practices, and SEO = 100; investigate any performance regression from the current static baseline.
7. Validate supporting artifacts with:
   - `npx --yes vnu-jar --errors-only docs/index.html docs/social.html`
   - `node --check docs/site.js`
   - `node Scripts/check-site-locales.mjs`
   - `node Scripts/generate-llms.mjs --check`
   - `git diff --check`
8. Confirm `docs/social.png` is 1200×630 and shows CodexWinBar/Windows identity, exactly seven integrations, no Gemini, and demo-data disclosure.

The public Pages URL may remain 404 until the repository owner separately enables branch-source Pages from `main`/`docs`; that is deployment state, not a local runtime failure.
