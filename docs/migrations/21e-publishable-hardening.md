# Phase 21e — Publishable Forms hardening (consumer migration)

**What changes.** Three defence-in-depth tightenings on the publishable-forms public surface shipped by [Phase 21b](21b-publishable-surveys-share-link-tokens-for-forms.md):

1. **H2 — mode-aware validator severity.** `PublishableFormConfigValidator` now emits **`Error`** (boot fails) in persistent-data modes (`Individual` / `Team` / `MultiTeam`) when a `Publishable` schema is registered without `ShareTokenStore` / `PublicBaseUrl`. `Anonymous` / `AuthenticatedEphemeral` preserve the prior `Warning`. The new escape hatch `ServerConfig.AcceptUnsignedPublishable = true` (env `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE=1`) downgrades persistent-mode `Error` back to `Warning` for staging-shape-in-production-mode.
2. **L1 — collapsed token-rejection messages.** `PublicEmbed.describeError` now renders a single user-visible string for every token failure (`Unauthorised`, `NotFound("token", _)`, `RateLimited`). Audit log server-side still distinguishes the cases; the embed no longer doubles as a token-state oracle.
3. **L2 — per-token rate-limit hook.** New optional `ShareTokenClaim.RateLimit` field + `IShareTokenRateLimiter` interface + `InMemoryShareTokenRateLimiter` default + `FormsServerApp.withShareTokenRateLimiter` builder + new `FormError.RateLimited` variant.

**Scope.** Forge SDK ships the validator severity change, the new claim field + issue-time validation, the new interface + in-memory default, the `withShareTokenRateLimiter` builder, the `IShareTokenRateLimiterContract` portability pack, and the new `FormError` variant. Consumer adoption is opt-in per deployment.

## Diff to apply (consumer side)

### 1. Decide the validator severity posture

If the deployment legitimately runs in a persistent-data mode with `Publishable` schemas registered but no token store wired (dry-run / staging dress-rehearsal), set the escape hatch:

```fsharp
let config = {
    ServerConfig.defaults with
        Mode = MultiTeam
        ShareTokenStore = NoShareTokenStore
        AcceptUnsignedPublishable = true  // ← downgrade to Warning
}
```

Or set the env var: `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE=1`.

Most production deployments do NOT want this — boot-failure on a missing token store is the safer default. The `Error` exists because a misconfigured production deployment that boots cleanly with only a `Warning` ships a public surface with no signed-token gate, no use-limit enforcement, and no revocation.

### 2. Surface `FormError.RateLimited` in your UI

The Fable.Remoting wire contract picks up the new DU case automatically. Client-side `match` statements over `FormError` are no longer exhaustive — add the case:

```fsharp
match err with
| FormError.Unauthorised | FormError.NotFound _ -> ...
| FormError.RateLimited ->
    // NEW: per-token rate limit exceeded. PublicEmbed renders the
    // same "link is no longer valid" string as the other token-
    // rejection cases; only the audit log distinguishes.
    ...
| ...
```

If you use the SDK's `PublicEmbed` Feliz component, no change is needed — the message collapse is internal.

### 3. (Optional) Issue tokens with per-token rate limits

For high-value publishable forms where a leaked token could be exercised to skew aggregations, set `RateLimit` on the issue request:

```fsharp
let issueRequest: ShareTokenIssueRequest = {
    ScopeId = scopeId
    ResourceKind = PublicFormApi.ResourceKind
    ResourceId = schemaId
    AttributedHandle = Some recipient.Handle
    IssuedBy = userId
    ExpiresAt = Some expiresAt
    UseLimit = Some (Some 10)
    RateLimit = Some {
        MaxUses = 5
        Window = TimeSpan.FromMinutes 1.0
    }
}
```

`MaxUses < 1` or `Window < 1s` is rejected at issue-time (`Error (ShareTokenError.StorageFailed _)`). Leave `RateLimit = None` to preserve pre-21e behaviour byte-for-byte.

### 4. Multi-replica deployments: wire a distributed `IShareTokenRateLimiter`

The SDK auto-defaults to `InMemoryShareTokenRateLimiter` (single-instance only — window state lives in a process-local dictionary). Multi-replica deployments must wire a distributed companion so per-token windows are shared across nodes:

```fsharp
let limiter : IShareTokenRateLimiter =
    MyCompany.RedisShareTokenRateLimiter(redisConnString) :> _

FormsServerApp.create ()
|> FormsServerApp.withShareTokenRateLimiter limiter
|> ...
```

The `IShareTokenRateLimiterContract` portability pack (`ToolUp.Forms.Tests/Contracts/IShareTokenRateLimiterContract.fs`) validates any impl against the same conformance bar.

## Verification steps

1. **`dotnet build` clean** on the consumer's sln. Adding the new `FormError.RateLimited` case is the most likely break point — fix non-exhaustive matches. The new `ServerConfig.AcceptUnsignedPublishable` field is part of the record so any anonymous-record literal construction of `ServerConfig` fails; use `{ ServerConfig.defaults with ... }` instead.
2. **Boot a persistent-mode deployment with `Publishable` schemas + `NoShareTokenStore`** and confirm startup fails with an `Error` preflight diagnostic naming `AcceptUnsignedPublishable`. Set the env var and confirm it boots with `Warning`.
3. **Issue a token with `RateLimit = Some { MaxUses = 5; Window = 1.0 min }`** and exercise the public-submit path 6 times rapidly; confirm submissions 1–5 succeed and the 6th returns `FormError.RateLimited`. Wait 61 seconds; confirm a fresh submission is admitted.
4. **`dotnet run --project Build.fsproj -- Verify`** clean in forge — the new `PublishableHardeningTests` (4 test groups including the contract pack) must be green.

## Rollback

H2 is opt-out via `AcceptUnsignedPublishable = true` (or the env var) — set it to preserve pre-21e boot behaviour for persistent modes. L1 is internal to `PublicEmbed.describeError`; rolling back requires reverting the consumer's adoption PR. L2 is additive — the new claim field is `option`-typed and defaults to `None`, so leaving it unset preserves byte-for-byte pre-21e behaviour. The auto-defaulted `InMemoryShareTokenRateLimiter` is consulted only when `RateLimit = Some _`; tokens issued without a rate limit skip the gate entirely.

The only non-reversible part is the new `FormError.RateLimited` DU case (consumers must add the match arm). If a regression in any of the three areas surfaces post-adoption, revert the consumer's adoption PR and pin `ToolUp.Forms.Server` to the pre-21e release.
