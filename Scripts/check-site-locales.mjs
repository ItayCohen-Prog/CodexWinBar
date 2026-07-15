#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const docsDir = path.join(repoRoot, "docs");
const indexPath = path.join(docsDir, "index.html");
const socialPath = path.join(docsDir, "social.html");
const cssPath = path.join(docsDir, "site.css");
const jsPath = path.join(docsDir, "site.js");
const indexHtml = fs.readFileSync(indexPath, "utf8");
const socialHtml = fs.readFileSync(socialPath, "utf8");
const siteCss = fs.readFileSync(cssPath, "utf8");
const siteJs = fs.readFileSync(jsPath, "utf8");
const canonical = "https://itaycohen-prog.github.io/CodexWinBar/";

assert(!fs.existsSync(path.join(docsDir, "CNAME")), "inherited CNAME must stay removed until a CodexWinBar domain is configured");
assert(indexHtml.includes(`<link rel="canonical" href="${canonical}"`), "missing CodexWinBar canonical URL");
assert(indexHtml.includes(`<meta property="og:url" content="${canonical}"`), "missing CodexWinBar Open Graph URL");
assert(indexHtml.includes("og:image:alt"), "missing Open Graph image alternative text");
assert(indexHtml.includes("twitter:image:alt"), "missing Twitter image alternative text");
assert(indexHtml.includes('class="skip-link" href="#main-content"'), "missing main-content skip link");
assert(indexHtml.includes('<main id="main-content">'), "missing main landmark target");
assert(count(indexHtml, /<h1\b/g) === 1, "site must contain exactly one h1");
assert(count(indexHtml, /class="provider-card"/g) === 7, "site must contain exactly seven shipping provider cards");
assert(indexHtml.includes("experimental Cursor integration"), "Cursor must be labeled experimental");
assert(indexHtml.includes("A fresh install connects nothing"), "provider opt-in disclosure is required");
assert(indexHtml.includes("taskbar overlay"), "shipping overlay behavior must be stated");

for (const provider of ["Codex", "Claude", "GitHub Copilot", "OpenRouter", "OpenAI Admin", "z.ai", "Cursor"]) {
  assert(indexHtml.includes(provider), `missing shipping provider ${provider}`);
}

const forbidden = [
  ["codexbar.app", "upstream domain"],
  ["https://codex.bar", "upstream redirect domain"],
  ["56 providers", "upstream provider count"],
  ["Gemini", "non-shipping provider"],
  ["Homebrew", "macOS install path"],
  ["WidgetKit", "macOS widget technology"],
  ["Sparkle", "macOS updater"],
  ["Download for macOS", "macOS download copy"],
  ["site-utilities.css", "generated upstream utility stylesheet"],
  ["site-locales.mjs", "upstream localization bundle"],
  ["language-picker", "upstream language picker"],
  ["github.com/steipete/CodexBar/releases", "upstream release download"],
];

for (const [needle, label] of forbidden) {
  assert(!indexHtml.includes(needle), `index.html still contains ${label}: ${needle}`);
}

assert(!indexHtml.includes("cdn.tailwindcss.com"), "site must not load Tailwind from a runtime CDN");
assert(!indexHtml.match(/<script\b[^>]*\bsrc=["']https?:\/\//i), "scripts must be local");
assert(!indexHtml.match(/<link\b[^>]*\brel=["']stylesheet["'][^>]*\bhref=["']https?:\/\//i), "stylesheets must be local");
assert(!siteJs.match(/^\s*import\s/m), "site JavaScript must not import a startup bundle");
assert(!siteCss.includes("overflow-x: hidden"), "horizontal overflow must not be concealed as a reflow fix");
assert(!siteCss.includes("overflow-x:hidden"), "horizontal overflow must not be concealed as a reflow fix");

for (const [file, html] of [[indexPath, indexHtml], [socialPath, socialHtml]]) {
  validateLocalReferences(file, html);
  validateImageDimensions(file, html);
}
assertPngDimensions(path.join(docsDir, "icon-64.png"), 64, 64);
assertPngDimensions(path.join(docsDir, "social.png"), 1200, 630);

for (const match of siteCss.matchAll(/url\((['"]?)([^)'"\s]+)\1\)/g)) {
  const value = match[2];
  if (value.startsWith("data:") || value.startsWith("#")) continue;
  validateLocalReference(cssPath, value);
}

console.log("CodexWinBar site OK: 7 providers, metadata, semantics, and local assets");

function validateLocalReferences(file, html) {
  for (const match of html.matchAll(/\b(?:src|href)=["']([^"']+)["']/g)) {
    const value = match[1];
    if (value.startsWith("#") || value.startsWith("mailto:") || /^https?:\/\//.test(value)) continue;
    validateLocalReference(file, value);
  }
}

function validateLocalReference(file, value) {
  const clean = value.split(/[?#]/, 1)[0];
  if (!clean) return;
  const resolved = path.resolve(path.dirname(file), clean);
  assert(resolved.startsWith(docsDir + path.sep), `${path.relative(repoRoot, file)} references a path outside docs: ${value}`);
  assert(fs.existsSync(resolved), `${path.relative(repoRoot, file)} references missing local asset ${value}`);
}

function validateImageDimensions(file, html) {
  for (const match of html.matchAll(/<img\b[^>]*>/gi)) {
    const tag = match[0];
    assert(/\bwidth=["']\d+["']/.test(tag), `${path.relative(repoRoot, file)} image is missing an intrinsic width: ${tag}`);
    assert(/\bheight=["']\d+["']/.test(tag), `${path.relative(repoRoot, file)} image is missing an intrinsic height: ${tag}`);
  }
}

function assertPngDimensions(file, expectedWidth, expectedHeight) {
  const bytes = fs.readFileSync(file);
  const signature = bytes.subarray(0, 8).toString("hex");
  assert(signature === "89504e470d0a1a0a", `${path.relative(repoRoot, file)} is not a PNG`);
  const width = bytes.readUInt32BE(16);
  const height = bytes.readUInt32BE(20);
  assert(width === expectedWidth && height === expectedHeight,
    `${path.relative(repoRoot, file)} must be ${expectedWidth}x${expectedHeight}, got ${width}x${height}`);
}

function count(value, pattern) {
  return [...value.matchAll(pattern)].length;
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
