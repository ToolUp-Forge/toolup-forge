# 1235 — declared content on pages and layouts (`server-app-host` 7.1)

**Class:** additive. No `ToolUp.*` package surface moves; the composition profile in `spec/` moves 7.0 → 7.1.

## What changes

A `layoutRef` or a `pageRef` may carry an OPTIONAL `content` member: an ordered, non-empty list of files of the
author's own, each `{ "source", "contentHash", "mode" }` (profile §P3.18).

- `source` — a relative path under the application's root, in §P4.32's grammar.
- `contentHash` — `sha256:` and 64 lower-case hex digits over the file's raw bytes.
- `mode` — closed (§P2.22): `inline` puts the bytes in the document head as a `<style>` (`.css`) or a `<script>`
  (`.js`); `serve` writes them to the public directory at `_declared/<node id>/<file name>` and serves them at that
  path, linking a `.css` and loading a `.js` from the head.

In the head, the host's own stylesheets come first, then the layout's content and the page's, in written order, then
the theme. A served file is public whatever the page's audience, so content that must be gated is inlined.

A composition with no `content` encodes byte for byte as at 7.0 and keeps its verdict.

## The refusals

| Class | Refused when |
|---|---|
| `content-malformed` (§P4.76) | an empty `content`; a hash not in lower-case `sha256:` form; a served file name with no extension; a serving node whose `id` is not one path segment |
| `content-source-invalid` (§P4.77) | an absolute or escaping `source`, or one outside §P4.32's grammar |
| `content-inline-unsupported` (§P4.78) | an `inline` entry that is not `.css` or `.js` |
| `content-duplicate` (§P4.79) | one node naming a `source` twice |
| `content-path-collision` (§P4.80) | two served files of one node with one name (ignoring ASCII case), or a page routed under `_declared` beside served content |
| `content-hash-mismatch` (§P4.81) | at reduction, a declared file absent or not hashing to its `contentHash` |

An unregistered `mode` is `token-unregistered`.

## What a consumer edits

An author declaring a stylesheet for a layout adds the member to the node (the hash is the file's own):

```diff
-{"kind":"layoutRef","name":"report","id":"layout-report","children":[]}
+{"kind":"layoutRef","name":"report","content":[{"source":"layouts/report/report.css","contentHash":"sha256:<64 hex>","mode":"inline"}],"id":"layout-report","children":[]}
```

A composition that pins a profile version moves its pin to 7.1 when it starts declaring content.

## What an evaluator must model

- The member on both kinds, after `viewSpec` on a layout and after `captures` on a page, omitted when empty.
- The five composition rules (§P4.76–§P4.80) and the reduction rule (§P4.81): read each file, check the hash, refuse
  before writing anything.
- The reduction: inline elements in the layout's head in the order above; served files written under the public
  directory at the fixed prefix. In forge that directory is `ServerConfig.PublicPath`, served by the static-files
  stage ahead of authentication, which serves only extensions ASP.NET Core's default content-type map knows. A
  client build that empties the public directory must not run after the reduction has written into it.
- Under forge's `SecurityHardening`, an inline script runs only when the deployment admits it
  (`ServerApp.withCspSourceMode` with `NonceCsp`, or `HashCsp` over the script's text).
- The vectors: `spec/fixtures/profile-server-app-host/declared-content-*.json` and `reject-content-*.json`. A vector
  whose manifest entry has `contentRoot` carries its files there; its sources resolve against that directory.

## Verification

```powershell
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Composition profile"
```

## Rollback

Remove the `content` members and pin 7.0; nothing else in a composition depends on them.
