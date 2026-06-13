# Phase 132 — fail-closed authorization defaults

**Ships in:** `ToolUp.Platform.Server` (the in-tree ToolUp.Remoting dispatcher
+ `Api.make` + a new admin path-prefix middleware).

Three changes make the dispatcher's authorization *defaults* fail closed. The
Phase 132 core (the `HasRole` / `PlatformAdmin` bridge) shipped first; this tail
adds the deny-on-miss default, the admin structural backstop, and the
un-emittable-role startup warning.

## What changes

### 1. `[<RequiresRole "PlatformAdmin">]` is now live (behaviour change)

Before, `IAuthContext.HasRole role` resolved against the always-empty
`user.Roles` of every first-party provider, so a `[<RequiresRole "PlatformAdmin">]`
method denied **every** caller — including real admins. The core bridge
(`ForgeAuthContext.HasRole`) now reads the server-resolved `ToolUp.PlatformRole`
(from `IPlatformAdminStore`), so the gate **allows genuine platform admins** and
denies everyone else. This is a net-safer behaviour change, not a silent
widening (GP 11): the gate moves from "deny everyone" to "allow real admins".

Any role string **other than** `"PlatformAdmin"` still resolves against the
empty `user.Roles` and therefore denies everyone — see the new warning (§4).

### 2. Deny-on-miss (classification-map miss no longer fails open)

When an `AuthContextResolver` is armed, a per-request classification-map miss now
defaults to **deny** (`Unclassified` → `Deny` in `AuthClassifier.evaluate`),
where it previously defaulted to `Public` (no auth). A new **round-trip startup
assertion** refuses to start when any classified field name does not round-trip
through the active `RouteBuilder` (the dispatcher keys authorization by the
route's trailing path segment; the classification map is keyed by field name).
So the deny-on-miss flip only ever changes behaviour for a key divergence that
the startup assertion now prevents anyway.

### 3. Admin structural backstop

A new `PlatformAdminAuthorizationMiddleware` denies non-`PlatformAdmin` callers
at the path prefix for the raw Giraffe admin handlers:

- `/api/_platform/admin/*` (ad-unit CRUD, premium-user list, rate-limit events),
- `/api/_platform/tenants/*` (tenant lifecycle — already dispatcher-gated; covered
  here as defence in depth),
- `POST|DELETE /api/_platform/users/*/premium` (premium grant / revoke).

The existing in-handler `canModifyPlatformConfig` checks remain (defence in
depth). The backstop is additive (GP 11) — it only ever *adds* a denial the
handler would also make. **Not covered** (so it never over-blocks): the public
`GET /api/_platform/users/me/premium-status` read, and the `_platform/encryption/*`
endpoint (which keeps its own role-OR-env-token emergency gate).

### 4. Un-emittable-role startup warning

When the **default** auth-context resolver is used, `Api.make` now emits a
startup warning (to stderr) if an API record carries a `[<RequiresRole "X">]`
for any role `X ≠ "PlatformAdmin"` — because the default first-party resolver
can only ever emit `"PlatformAdmin"`, so such a gate denies every caller (a dead
gate). It **warns, does not refuse**: a consumer mid-migration may be about to
wire a custom `?authContext` resolver that emits the role.

## Diff to apply

**Nothing for most consumers** — the deny-on-miss and admin backstop are wired
inside `ToolUp.Platform.Server` and apply automatically on upgrade.

- **If a consumer relied on `[<RequiresRole "PlatformAdmin">]` denying everyone**
  (an unlikely, accidental dependency on the dead gate) — those methods now admit
  genuine platform admins. No action needed unless that side effect was load-bearing.
- **If a consumer supplies a custom `RouteBuilder`** to `Api.make` whose trailing
  path segment differs from the method name — the dispatcher now **refuses to
  start** with a diagnostic naming the offending fields. Use a route builder whose
  trailing segment equals the method name (the default `/api/{type}/{method}` and
  any `…/{method}` shape already satisfy this).
- **If a consumer uses `[<RequiresRole "SomethingElse">]` as a sole gate against
  the first-party providers** — that gate is dead (denies everyone). Either supply
  a custom `?authContext` resolver that emits the role, or gate on
  `"PlatformAdmin"` / a `[<RequiresClaim>]`. The new startup warning surfaces this.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` —
  the Phase 132 cases in `AuthorizationTests.fs` cover: classification-miss-denies,
  the round-trip assertion (default + tenant custom builder pass; a suffixing
  builder is flagged), `unemittableRoles`, and the admin path-prefix backstop
  (non-admin → 403 before the handler; admin passes; public read untouched).
- A non-admin request to `/api/_platform/admin/ad-units` returns **403** even when
  the handler omits its in-line check; an admin passes; the in-handler checks still
  fire (no regression).
- Boot a deployment whose API record names a non-`PlatformAdmin` role: confirm the
  `[ToolUp.Remoting] WARN:` line on stderr at startup.

## Rollback

Revert the SDK version pin. The admin backstop and deny-on-miss are server-side
only (no wire-format change); the round-trip assertion only affects startup of
deployments with a divergent custom `RouteBuilder`. No persisted state changes.
