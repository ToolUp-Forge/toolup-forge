# Migration 274 — hosted-tree content sanitization seam (CSP-aligned)

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

A hosted tree (Phase 110 / 111) carries rich content — markdown, code blocks, raw HTML — and once an
AI or an untrusted server emits the tree, that content can inject unsafe HTML (`<script>`,
`<iframe>`, a `javascript:` URL) or violate the deployment's Phase 9j CSP. The host surface shipped no
sanitization seam. This phase ships a neutral **default-deny** sanitizer that makes hosted content
safe by construction, aligned to the CSP.

New surface in `src/ToolUp.Platform.Core/Shared/Types/HostContentSanitizer.fs` (namespace
`ToolUp.Platform`):

- `HostContentKind` — `Html | Markdown | Code | PlainText`. The neutral content-kind tag (no
  tree-language type — GP 1).
- `SanitizedContent` — `{ Html: string; Modified: bool }`. `Modified` flags that something unsafe was
  removed / neutralised, so a caller can log rather than silently swallow.
- `HostSanitizePolicy` — `{ AllowedTags; AllowedAttributes; AllowedUrlSchemes }`. The allow-list;
  everything off it is denied. `HostSanitizePolicy.default'` is the CSP-aligned default (common
  text/structural tags, safe attributes, `http`/`https`/`mailto`/`tel` schemes only).
- `IHostContentSanitizer` — `Sanitize : HostContentKind -> string -> SanitizedContent`. Pure +
  deterministic, so the client (Phase 110) and SSR (Phase 111) legs sanitize **byte-identically** (no
  hydration mismatch).
- `HostContentSanitizer.create policy` / `HostContentSanitizer.default'` — build a sanitizer. The
  default is the **safe** one; a consumer may supply a *stricter* policy, never a weaker silent
  default (GP 2).

What the default strips (mirrors a hardened CSP): `<script>` / `<style>` / `<iframe>` / `<object>` /
`<embed>` / `<noscript>` / `<template>` elements (content dropped); `<link>` / `<meta>` / `<base>`;
`on*` handler attributes; inline `style`; `javascript:` / `vbscript:` / `data:` (and control-char
obfuscations like `java\tscript:`) URL schemes in href/src; unknown tags (unwrapped) and unknown
attributes (dropped). `Markdown` is HTML-escaped first (embedded raw HTML becomes inert text) then a
small safe transform set (bold / italic / code / scheme-checked links) is applied. `Code` /
`PlainText` are escaped verbatim.

BCL-only (a hand-rolled scanner, no regex), so it ships under `fable/` and runs identically on both
render legs.

## How to adopt (opt-in)

```fsharp
let sanitizer = HostContentSanitizer.default'   // or HostContentSanitizer.create stricterPolicy

// In the hosted-render path, before injecting content:
let safe = sanitizer.Sanitize HostContentKind.Markdown aiEmittedMarkdown
// safe.Html is CSP-safe; safe.Modified tells you whether anything was stripped.
```

A deployment that renders no hosted content never constructs a sanitizer and is byte-for-byte
unchanged (GP 11/13).

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostContentSanitizer"
cd samples/MinimalClient && dotnet fable -o output   # the Core seam compiles under Fable
```

## Rollback

Delete `HostContentSanitizer.fs` + its `<Compile>` entry, `InProcess/HostContentSanitizerTests.fs` +
its `<Compile>` and `Program.fs` registration. No runtime impact on any deployment that never
sanitized hosted content.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in content-safety seam for hosted
trees. No current matrix consumer renders hosted rich content; a deployment that composes no
sanitizer is byte-for-byte unchanged (GP 11/13).
