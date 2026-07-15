#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const docsDir = path.join(repoRoot, "docs");
const args = process.argv.slice(2);
const mode = parseMode(args);
const origin = "https://itaycohen-prog.github.io/CodexWinBar/";
const productName = "CodexWinBar";
const source = "https://github.com/ItayCohen-Prog/CodexWinBar";
const outputPath = path.join(docsDir, "llms.txt");

const pages = allHtml(docsDir)
  .map((file) => {
    const rel = path.relative(docsDir, file).replaceAll(path.sep, "/");
    if (rel === "404.html" || rel === "social.html") return null;
    const html = fs.readFileSync(file, "utf8");
    return {
      rel,
      title: textContent(html.match(/<title[^>]*>([\s\S]*?)<\/title>/i)?.[1]) || titleize(path.basename(rel, ".html")),
      description: attr(html.match(/<meta\s+name=["']description["']\s+content=["']([^"']*)["'][^>]*>/i)?.[1] || ""),
    };
  })
  .filter(Boolean)
  .sort((a, b) => (a.rel === "index.html" ? -1 : b.rel === "index.html" ? 1 : a.rel.localeCompare(b.rel)));
const productDescription =
  pages.find((page) => page.rel === "index.html")?.description ||
  "CodexWinBar shows supported AI coding usage limits from a Windows 11 taskbar overlay and flyout.";

const lines = [
  "# " + productName,
  "",
  productDescription,
  "",
  "Canonical documentation:",
  ...pages.map((page) => "- " + page.title + ": " + pageUrl(page.rel) + (page.description ? " - " + page.description : "")),
  "",
  "Source: " + source,
  "",
  "Product scope:",
  "- Windows 11 x64 taskbar overlay with a native WPF flyout.",
  "- Seven shipping integrations: Codex, Claude, GitHub Copilot, OpenRouter, OpenAI Admin, z.ai, and experimental Cursor.",
  "- Fresh installs connect nothing; users explicitly connect each provider.",
  "",
  "Guidance for agents:",
  "- Prefer the canonical landing page and shipping Windows source over stale inherited macOS documentation.",
  "- Do not infer provider support from dormant source files or logo assets.",
  "- Fetch only the pages needed for the current task; this is an index, not a full-site corpus.",
  "",
];
const output = lines.join("\n");

if (mode === "check") {
  const current = fs.existsSync(outputPath) ? normalizeNewlines(fs.readFileSync(outputPath, "utf8")) : null;
  if (current !== output) {
    console.error(`${path.relative(repoRoot, outputPath)} is out of date; run node Scripts/generate-llms.mjs`);
    process.exit(1);
  }
  console.log("llms index OK: " + path.relative(repoRoot, outputPath));
} else {
  fs.writeFileSync(outputPath, output, "utf8");
  console.log("wrote " + path.relative(repoRoot, outputPath));
}

function parseMode(values) {
  if (values.length === 0) return "write";
  if (values.length === 1 && (values[0] === "write" || values[0] === "--write")) return "write";
  if (values.length === 1 && (values[0] === "check" || values[0] === "--check")) return "check";
  console.error("Usage: node Scripts/generate-llms.mjs [write|--write|check|--check]");
  process.exit(2);
}

function allHtml(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.name === "node_modules" || entry.name.startsWith(".")) return [];
    if (entry.isDirectory()) return allHtml(full);
    return entry.name.endsWith(".html") ? [full] : [];
  });
}

function pageUrl(rel) {
  return rel === "index.html" ? origin : origin + rel;
}

function textContent(value) {
  return attr(value || "").replace(/<[^>]+>/g, "").replace(/\s+/g, " ").trim();
}

function attr(value) {
  return String(value || "")
    .replace(/&mdash;/g, "-")
    .replace(/&amp;/g, "&")
    .replace(/&nbsp;/g, " ")
    .replace(/&#39;/g, "'")
    .replace(/&quot;/g, '"')
    .trim();
}

function normalizeNewlines(value) {
  return value.replace(/\r\n?/g, "\n");
}

function titleize(input) {
  return input.replaceAll("-", " ").replace(/\b\w/g, (match) => match.toUpperCase());
}
