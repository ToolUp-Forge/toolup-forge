# Migration — Phase 129: Secure-by-default request edge

**Status:** behavioural-on-upgrade hardening of the request/response edge. Four controls that were present-but-off-by-default become the no-config outcome, surfaced as preflight refusals (not silent defaults) plus an always-on response-header floor. A genuinely-internal/dev deployment, or one that already set the relevant config, keeps a byte-for-byte-identical posture; an internet-facing auth deployment gains protection without operator action and, in two cases, must consciously acknowledge a downgrade to keep the old behaviour.

Shipped in two parts: **129a/b/c** (forge `4845c66`) — dev-admin escalation, OAuth redirect-base refusal, CSRF preflight; **129d** (this doc's commit) — the security-headers baseline floor and the CSRF `Warning`→`Error` escalation with its typed opt-out.

## What changes

### 129d — security-headers baseline floor (new observable response change)

`SecurityHeadersMiddleware` now stamps an always-on baseline floor on **every** response of **every** deployment, beneath the consumer's `ServerConfig.SecurityHeaders` map and beneath per-route handler-set headers:

- `X-Frame-Options: DENY`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Strict-Transport-Security: max-age=31536000; includeSubDomains` — **only when `RequireHttps`** (the bind enforces HTTPS at this layer).

Previously `SecurityHeaders` defaulted to `Map.empty` and the middleware no-op'd, so a stock deployment shipped framable (clickjacking → state-changing actions) and sniffable. The floor closes that by default (GP 9 — no silent insecure default).

Deliberately **no `Content-Security-Policy`** in the floor — a default CSP breaks real apps (inline styles, third-party widgets). CSP stays the opt-in `SecurityHardening` path (companion-aware `CspMiddleware`) and the richer `SecurityHeaders.productionDefaults` map.

**Priority (lowest → highest):** the 129d floor → the consumer's `SecurityHeaders` map → a per-route handler that already wrote the header. The middleware only sets a key not already present on the response, and `SecurityHeaders.effective` merges the consumer map *over* the floor — so a consumer that already set `X-Frame-Options` (or any floor key) is **byte-for-byte unchanged** (GP 11). The merge logic is pure (`SecurityHeaders.effective requireHttps configured`) and unit-tested.

The `SecurityHeadersValidator` (Phase 6l.K) is narrowed to match: it now warns only when an internet-facing auth deployment ships *no CSP* — empty `SecurityHeaders` **and** `NoSecurityHardening` — since the floor now covers the other baseline headers. A hardening-enabled deployment no longer trips a spurious "missing headers" warning.

### 129c/129d — CSRF off-by-default under cookie auth (`Warning` → `Error`)

`CsrfDefaultModeValidator` (security-class) now returns **`Error`** — a hard startup refusal — when the resolved deployment requires auth AND `SseAuthMode = CookieRequired` AND `SecurityHardening = NoSecurityHardening`. Cookie-authenticated mutations otherwise have no server-side CSRF check; the only protection is the client cookie's `SameSite=Strict`, which is browser-version-dependent and subdomain-bypassable. (129a/b/c shipped this as a `Warning`; 129d escalates it now that the typed opt-out exists.)

New typed opt-out — the standard `Accept…WhenAuthRequired` escape-hatch idiom:

```fsharp
ServerConfig.AcceptSameSiteOnlyCsrfWhenAuthRequired: bool   // default false
// env: TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE=1
```

Set it `true` to acknowledge a deliberate SameSite-only / out-of-band-CSRF posture; the refusal downgrades to `Ok`. The SDK does **not** auto-enable hardening (that would break existing cookie-auth deployments that manage CSRF out of band) — it refuses-and-names instead.

### 129a — dev-admin escalation (shipped `4845c66`)

`AutoBootstrapDevAdminModeValidator` returns `Error` (not `Warning`) when the deployment looks internet-facing (`RequireHttps`), keeping `Warning` for the pure-local auth-dev case. A leaked dev `AutoBootstrapDevAdmin` value in a production OIDC/Clerk deployment (empty admin list + unset `TOOLUP_INITIAL_PLATFORM_ADMIN` → first sign-in becomes Platform Admin) now refuses startup.

### 129b — OAuth redirect-base (shipped `4845c66`)

`OAuthFlowValidator` promotes the `TOOLUP_OAUTH_REDIRECT_BASE`-unset case from `Warning` to `Error` in authenticated modes, so a spoofed/forwarded `Host` can no longer repoint the provider `redirect_uri` with only the provider's exact-match URI registration as a backstop.

## Consumer action

Audit, per pinned consumer:

1. **Cookie auth without hardening?** A deployment on `SseAuthMode = CookieRequired` + `NoSecurityHardening` now refuses startup. Preferred fix: `withSecurityHardening` (mounts the server-side double-submit CSRF check). If you genuinely rely on SameSite-only / out-of-band CSRF, set `AcceptSameSiteOnlyCsrfWhenAuthRequired = true` (`TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE=1`). A `TokenLocation = Cookie / BearerOrCookie` wired directly on the auth provider (not visible on `ServerConfig`) is the same exposure class — enable hardening there too.
2. **Response headers now present.** Responses now carry `X-Frame-Options: DENY` / `nosniff` / `Referrer-Policy` (+ HSTS on HTTPS) even with `SecurityHeaders = Map.empty`. If you legitimately embed your app in an iframe, set your own `X-Frame-Options` (or a `frame-ancestors` CSP) in `ServerConfig.SecurityHeaders` — your value wins over the floor. If you front the app with a CDN/proxy that already injects these, the handler/proxy value wins (the middleware skips a key already present); no action needed.
3. **Relied on the warn-only redirect-base / dev-admin?** If startup now refuses, set `TOOLUP_OAUTH_REDIRECT_BASE` explicitly, and leave `AutoBootstrapDevAdmin = None` + set `TOOLUP_INITIAL_PLATFORM_ADMIN` for production.

An Anonymous deployment, or one already on hardening / with its own headers set, needs no action.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`:
  - `SecureByDefaultValidatorTests.fs` — cookie auth + `NoSecurityHardening` + auth surface → `Error` naming `AcceptSameSiteOnlyCsrfWhenAuthRequired`; the acknowledgement flag → `Ok`; hardening enabled → `Ok`; non-cookie / anonymous → `Ok`.
  - `SecurityHeadersValidatorTests.fs` — `SecurityHeaders.effective` returns the three-header floor (+ HSTS only under `RequireHttps`), the consumer map wins on a key collision, and the floor is never empty; the validator no longer warns when hardening provides a CSP.
- Startup-log / response-header spot check: a stock auth deployment's responses carry `X-Frame-Options` / `nosniff` / `Referrer-Policy` (and HSTS on HTTPS); a consumer-set frame header is not overwritten.

## Rollback

- Headers floor: revert `SecurityHeaders.effective` to return the configured map unchanged (and restore the middleware's `if not config.SecurityHeaders.IsEmpty` guard). Responses return to no default headers — framable/sniffable by default.
- CSRF: change `CsrfDefaultModeValidator`'s `Error` back to `Warning` and drop the `AcceptSameSiteOnlyCsrfWhenAuthRequired` clause. Cookie-auth-without-hardening returns to a non-aborting warning — roll back only with the CSRF exposure understood.
