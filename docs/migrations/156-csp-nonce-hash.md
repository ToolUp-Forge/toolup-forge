# Migration — Phase 156: Per-request nonce / per-content hash CSP sources

**Status:** opt-in CSP hardening. Default behaviour is byte-for-byte pre-156 (GP 11) — a deployment that does not call `withCspSourceMode` resolves the same static `Content-Security-Policy` header as before, and pays nothing per request (GP 13). Adopt only if you run a strict CSP (`withSecurityHardening`) and need to cover your own SSR-emitted inline `<script>` / `<style>` without `'unsafe-inline'`.

## What changes

Forge's `CspMiddleware` previously stamped a **static** aggregated CSP header — no per-request nonce, no per-content hash. A layout with any inline script then had to either weaken the policy with `'unsafe-inline'` or externalise every script. Phase 156 adds the two CSP source modes browsers accept, selected by a new `SecurityHardening.CspSourceMode` and composed with `ServerApp.withCspSourceMode`:

```fsharp
type CspSourceMode =
    | StaticCsp                          // default — the pre-156 static header
    | NonceCsp                           // per-request random nonce
    | HashCsp of inlineScripts: string list   // sha256 over declared inline scripts
```

### Nonce mode (`NonceCsp`) — for dynamic responses

`CspMiddleware` mints a cryptographically-random per-request nonce, substitutes it into the header's `script-src` / `style-src` `'nonce-…'` sources, and stashes it on `HttpContext.Items`. Layouts read it to stamp matching inline tags:

```fsharp
// in your SSR layout (server-side, ToolUp.Platform.Csp):
match Csp.requestNonce ctx with
| Some nonce -> script [ _nonce nonce ] [ rawText inlineJs ]   // <script nonce="…">
| None       -> script [] [ rawText inlineJs ]                  // mode not composed
```

Every response carries a unique `script-src 'nonce-…'`, so a stamped inline script executes under a policy with **no `'unsafe-inline'`**. Nonce mode pairs naturally with `StrictSecurityHardening` (which already drops `'unsafe-inline'`); under `DefaultSecurityHardening` the presence of a style nonce causes browsers to ignore `'unsafe-inline'` for `style-src` per CSP3 — i.e. you are moving to nonce-governed inline styles, so stamp your inline `<style>` too.

### Hash mode (`HashCsp`) — for cached / deterministic responses

The CSP carries `'sha256-…'` source hashes computed over a declared set of inline-`<script>` bodies, folded into `script-src`. The header is **byte-identical across requests**, so it survives HTML-body caching + `304`s. You hand the exact inline-script bodies you emit; the hash is over those bytes, so the emitted `<script>` must match byte-for-byte:

```fsharp
let bootstrapJs = "window.__APP__ = JSON.parse(document.getElementById('state').textContent)"
app
|> ServerApp.withSecurityHardening StrictSecurityHardening
|> ServerApp.withCspSourceMode (SecurityHardening.HashCsp [ bootstrapJs ])
// emit <script>{bootstrapJs}</script> verbatim in your layout — no nonce attribute
```

## The nonce ↔ cache decision (load-bearing)

A nonce baked into a response body must match the header that body is served with. Nonce-CSP and HTML-body caching are in genuine tension:

| Response shape | Mode | Why |
|---|---|---|
| **Dynamic** (re-rendered per request) | `NonceCsp` | A fresh nonce per request, header and body always agree. |
| **Cached / deterministic** (render cache, `304`, prerender) | `HashCsp` | Header is byte-stable, so a stored body's sources stay valid across hits. |

Two safeguards enforce this:

1. **`304` skip (middleware).** In nonce mode, `CspMiddleware` does **not** stamp a fresh-nonce header on a `304 Not Modified` — the browser is revalidating a body it already holds, whose cached CSP header carries the original nonce; a new nonce would overwrite it and mismatch the cached body. Static / hash mode is byte-stable, so its header is always safe to stamp.
2. **Startup validator.** `CspNonceCacheValidator` (`csp-nonce-render-cache`) **warns** at preflight when `NonceCsp` is composed alongside a registered `IRenderCache` (Phase 84/155). A render-cache *hit* serves a stored 200 body with a fixed nonce that `CspMiddleware`'s fresh per-request header nonce would mismatch — a silent break (dead inline script, no error). The warning steers you to `HashCsp`. The detection is by full type name (`ToolUp.PublicRendering.IRenderCache`) so `ToolUp.Platform.Server` keeps no dependency on the upper-layer rendering companion.

## Consumer action

None required to stay on the prior behaviour — `StaticCsp` is the default and resolves the same header as pre-156.

To adopt, per pinned consumer:

1. **Decide nonce vs hash by cacheability.** Dynamic SSR → `NonceCsp`; cached/deterministic/prerendered SSR → `HashCsp`. If you compose `withRenderCache`, use `HashCsp` (or expect the startup warning).
2. **Compose the mode** alongside hardening:
   ```fsharp
   app
   |> ServerApp.withSecurityHardening StrictSecurityHardening
   |> ServerApp.withCspSourceMode SecurityHardening.NonceCsp
   ```
3. **Stamp your inline tags.** Nonce mode: read `Csp.requestNonce ctx` and set `nonce="…"` on every inline `<script>` / `<style>`. Hash mode: declare every inline-script body in the `HashCsp` list and emit it byte-for-byte.
4. **Verify no `'unsafe-inline'` reliance remains** for your own inline content — under nonce/strict the browser ignores `'unsafe-inline'` once a nonce/hash is present in that directive.

A deployment with no inline scripts/styles, or one happy with `'unsafe-inline'`, needs no change.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` green (the `ToolUp.Platform.Tests` pack covers: static header byte-for-byte pre-156; nonce placeholder folded into `script-src`/`style-src` + unique substituted nonce per request + `304` suppression; hash header byte-stable across requests; validator fires on nonce+cache and stays `Ok` otherwise).
- Manual: with `NonceCsp`, confirm each response's `Content-Security-Policy` `script-src 'nonce-…'` matches the `nonce="…"` on the rendered inline `<script>`, and the script executes (no console CSP violation). With `HashCsp` + a render cache, confirm the header is identical on a cache hit and a following `304`.

## Rollback

Remove the `withCspSourceMode` call (or set `StaticCsp`). The resolved CSP header reverts to the pre-156 static aggregation byte-for-byte, and the per-request nonce path is no longer taken. No persisted state, no wire-format migration.
