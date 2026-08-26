# Migration — Phase 336: fail-closed dispatch consistency

**Status:** two request-gate behaviour changes, both strictly *more* closed. No public API changed;
no signature, route, config field, or wire contract moved. **A correctly-composed, lowercase-path
deployment is byte-for-byte unchanged and needs no consumer action.** Read §3 before upgrading only
if you compose a custom middleware pipeline or you route `_platform` premium writes yourself.

## Why

Every other decision point in the dispatch-authorization layer fails closed — the surface registry's
`strictDefault` is `userOrTeam` (design §3.0 OQ6), the Phase 69d classifier refuses to *start* on an
unclassified method, `ScopeResolutionMiddleware` downgrades a resolver `Error` to a synthetic
anonymous subject rather than to nothing. Two seams did not, and both sat on the primary `/api/*`
path.

## 1. `SurfaceEnforcementMiddleware` — a missing `Subject` is now evaluated, not passed through

**Before.** When `HttpContext.Items["ToolUp.Subject"]` was absent on an `/api/*` request, the
middleware called `next.Invoke(ctx)`. The comment argued the branch was reachable only via an
unsupported `/api` rewrite after scope resolution.

**That argument was wrong, and the counterexample is in the same repo.**
`ScopeResolutionMiddleware` wraps its whole resolution block in a `try`, and its `with` handler
logs, emits an `AuthScopeResolutionFailed` audit row, and continues — **without stashing a
`Subject`**. The comment at that catch says "the fail-closed behaviour is preserved (we don't
re-raise)". It was not preserved: the gate downstream read no Subject and let the request through
unauthenticated. So a DI hiccup, a store throw, or a cache miss-and-throw silently converted the
deployment's primary authentication gate into a pass-through — at exactly the moment it most needed
to hold, and with an audit row that said "resolution failed" rather than "the request was admitted
anyway".

**After.** The middleware synthesises a fresh `AnonymousSession` subject and runs the **same** §3.1
matrix against it. Concretely:

| Route requirement | Before (no Subject) | After |
|---|---|---|
| `userOrTeam` (the strict global default) | reached the handler | `401 authentication_required` |
| `teamScoped` | reached the handler | `401 authentication_required` |
| `claimBearerOnly` | reached the handler | `401 authentication_required` |
| `public_` (`/api/csrf-token`, peer prefixes, an Anonymous-only deployment's `/api/` catch-all) | reached the handler | **reaches the handler — unchanged** |
| `anonymousOnly` (sign-up flows) | reached the handler | **reaches the handler — unchanged** |
| any non-`/api/*` path | reached the handler | **reaches the handler — unchanged** |

Two design points worth stating because they are what keep this additive:

- **Synthesise, don't hard-401.** A blanket 401 would have closed `public_` and `anonymousOnly`
  routes, which legitimately admit `AnonymousKind`. Running the ordinary matrix means the denial
  set grows by exactly the routes that already refuse anonymous callers — and the denial uses the
  error codes, status codes, JSON body shape, `SurfaceDenied` audit row and `IAuthAuditHook`
  denial row the matrix already emits. There is no new response shape for a client to learn.
- **The synthetic session id is a fresh GUID, never caller-supplied.** This path is reached only
  when something has already gone wrong, so honouring a claimed `X-User-Id` / share token here
  would hand an attacker the victim's scope via a *failed* resolution — the Phase 337 hazard, in
  the softest possible place. Forged identity headers are pinned as ineffective by
  `FailClosedDispatchTests`.

A best-effort `ILogger.Warn` now names the method and path when this branch is taken. The pre-336
fall-through was structurally silent: an unauthenticated pass-through looked exactly like a normal
200 in every log and dashboard.

## 2. `PlatformAdminAuthorizationMiddleware` — the premium discriminator matches the prefix guard

**Before.** The `UsersPrefix` arm of `requiresPlatformAdmin` was internally inconsistent. Its prefix
test used `PathString.StartsWithSegments`, which is `OrdinalIgnoreCase` by default; its suffix test
was a case-**sensitive** `pathValue.EndsWith "/premium"` with no trailing-slash handling; and its
method test was an ordinal `httpMethod = "POST"`. So the two halves of one `if` disagreed about
casing, and `/api/_platform/users/{id}/PREMIUM`, a trailing `/premium/`, or a lower-case `post`
satisfied the prefix and then skipped the backstop entirely.

**After.** Method comparison is `OrdinalIgnoreCase`; the path is trailing-slash-trimmed (every
trailing slash, not one) and compared `OrdinalIgnoreCase`.

**Honest scope: this was latent, not a live end-to-end bypass.** Today's grant/revoke routes are
Giraffe `routef`, whose regex match *is* case-sensitive, so `/PREMIUM` 404s before reaching a
handler; and the in-handler `AccessContext.canModifyPlatformConfig` check still refuses the
lower-case-method variant. That is precisely why it was worth fixing rather than shrugging at: this
middleware exists to hold **when the other gate does not**. A future handler mounted with `routeCif`,
or through ASP.NET Core endpoint routing (case-insensitive by default), turns latent into live with
no edit to this file — and the whole premise of the Phase 132 backstop is that a handler added later
may forget its in-line check.

Normalising is strictly more closed, never less. The deliberately-open surfaces are unaffected on
both guards:

- `GET /api/_platform/users/me/premium-status` — still open; `premium-status` does not end
  `/premium` after the trailing-slash trim, and it is not a mutating method.
- `/api/_platform/encryption/*` — still outside the backstop (it keeps its role-OR-env-token
  scripted-recovery gate).
- `/api/_platform/ads/*`, `/api/_platform/consent` — still open.

## 3. Consumer action

**None, for a deployment composed through `ServerApp.run` / `AIServerApp.run` / `RAGServerApp.run`
with default pipeline ordering.** `ConfigurePipeline` registers `ScopeResolutionMiddleware` before
both middlewares, so `Subject` is always stashed for `/api/*` and §1's branch is unreachable in
normal operation.

Check two things only if they apply to you:

1. **You inject custom middleware via the `PreMiddleware` seam that rewrites a request path to add
   an `/api` prefix.** That was already unsupported; it now fails closed (401) instead of passing
   through unauthenticated. Rewrite before `ScopeResolutionMiddleware`, or declare the route
   `public_` if it is genuinely anonymous.
2. **You have a test or monitor that asserts a request WITHOUT a resolved `Subject` reaches your
   handler.** It now sees a 401 on any route that does not admit `AnonymousKind`. That assertion was
   pinning the defect; update it. (The SDK's own `SurfaceEnforcementMiddlewareTests` carried exactly
   such a case and was amended in this phase.)

If §1's new `ILogger.Warn` line ever appears in your logs, treat it as a signal that
`ScopeResolutionMiddleware` swallowed a resolver exception — correlate with the
`AuthScopeResolutionFailed` audit row emitted at the same instant. Before this phase, that
combination produced a 200.

## Verification

- `src/ToolUp.Platform.Tests/InProcess/FailClosedDispatchTests.fs` — 19 cases across both seams.
  Each deny arm is paired with its correct-path control (`public_` and `anonymousOnly` still
  admitted without a Subject; `premium-status` still open; a `PlatformAdmin` caller still admitted
  on every casing variant), so "everything is refused" cannot pass as a fix.
- Non-vacuity was demonstrated by reverting both gates: 9 of the 19 fail — exactly the four
  surface-gate deny cases and the five PlatformAdmin casing cases — plus the amended legacy case in
  `SurfaceEnforcementMiddlewareTests`, while all ten correct-path controls stay green.
- `dotnet build ToolUp.Forge.sln` clean; `dotnet run --project Build.fsproj -- VerifyAll` green.
