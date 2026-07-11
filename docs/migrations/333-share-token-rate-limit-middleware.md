# Share-token rate limit enforced in `ShareTokenAuthMiddleware`

**Ships in:** ToolUp.Platform.Server (`ShareTokenAuthMiddleware`).

## What changes

`ShareTokenAuthMiddleware` now enforces the per-token rate limit a share-token
claim declares. When the resolved `ShareTokenClaim` carries a
`RateLimit: ShareTokenRateLimit` **and** an `IShareTokenRateLimiter` is
registered in DI, the middleware calls
`Admit(claim.ScopeId, claim.TokenId, rate)` before continuing the pipeline. A
denial short-circuits with **HTTP 429** plus a `Retry-After` header (the
claim's `RateLimit.Window` in seconds, an upper bound on when the rolling
window frees a slot) and a JSON body in the middleware's existing error shape
(`{"error":"rate_limited","status":429,…}`).

Previously the limit was enforced only by consumers that called `Admit`
themselves (the public-forms handler) — any other claim-bearer route honoured
the claim but applied **no per-token rate limit**, so a leaked rate-capped
share link could be replayed unbounded against those routes.

Division of responsibility, unchanged where it was already right:

- the **middleware** enforces `RateLimit` (this change);
- the **consuming handler** still calls `IShareTokenStore.MarkUsed` after the
  consuming operation succeeds — a token whose downstream operation fails does
  not burn a use-slot.

## Diff to apply

Nothing, for most consumers — the change is additive and the gate is doubly
opt-in (GP 13):

- claims without a `RateLimit` skip the gate entirely;
- deployments without a composed `IShareTokenRateLimiter` are byte-for-byte
  unchanged (GP 11).

A deployment that issues rate-capped tokens **and** composes a limiter gets
the enforcement automatically on upgrade. A consumer that also calls `Admit`
in its own handler (the Forms pattern) now consumes **two** admissions per
request on routes behind the middleware; if that route is middleware-fronted,
drop the handler-side `Admit` call or double the token's `MaxUses`. (The
Forms public-form API is dispatched off its own token parameter, not the
middleware claim, so today's Forms flow is unaffected.)

## Verification

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
  -- --filter-test-list "Phase 333"` — 429-on-exhaustion, no-`RateLimit`
  pass-through, and no-limiter pass-through.
- Manually: issue a token with `RateLimit = Some { MaxUses = 2; Window = 60s }`,
  replay it 3× inside a minute against any claim-bearer route behind the
  middleware — the third response is 429 with `Retry-After: 60`.

## Rollback

Revert the SDK version pin. No persisted format is touched; behaviour returns
to handler-side-only enforcement.
