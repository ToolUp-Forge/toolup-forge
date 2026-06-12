# Phase 69g.tail — per-method `[<RateLimit>]` attribution + denial telemetry / audit (consumer migration)

**What changes.** Expensive API record methods opt into **dispatcher-enforced** rate limiting via `[<RateLimit(count, windowSeconds)>]` on the API record field. The dispatcher evaluates each budget per call against the composed `IRateLimitStore`, keyed per subject (the resolved auth-context `SubjectId`, or a per-IP fallback for anonymous callers). On denial it returns `429` + a `Retry-After` header + an `ErrorCategory.RateLimit` envelope, emits `MethodOutcome.RateLimited` telemetry (a bridged `IMetricsSink` records it distinctly from handler errors), and emits a `RateLimitExceeded` audit row — regardless of whether the method also carries `[<Audit>]`. Multi-attribute is **AND**: a method with both a per-second and a per-hour budget must pass both (short-burst and sustained traffic are independently capped).

**Scope.** Additive, **dormant by default** (GP 13): a method's `[<RateLimit>]` does nothing until an `IRateLimitStore` is composed, so an existing deployment that upgrades stays byte-for-byte identical until it wires a store. Compose the in-process default with `Remoting.withRateLimitStore (InMemoryRateLimitStore())`; distributed deployments wire Redis / DynamoDB / etc. against the same `IRateLimitStore` contract (the six portability rules — string keys, async, retry-as-data).

**Operator surface.** The `IServiceStatusBoard` "Rate-limit budgets" section reports per-method denial activity at the platform scope (the `RateLimitExceeded` audit rows) over the last 24h. Per-tenant-scoped denials surface under their own tenant scope — the same cross-scope read-cost limitation noted for the outbound limiter; a global live-utilisation view would require extending the (deliberately write-only) `IRateLimitStore` contract with a read surface, a future phase.

## Diff to apply

```fsharp
open ToolUp.Platform // tier-shared mirror, Fable-safe on Core API records

type MyApi = {
    // 30 calls/minute per subject. Use the RateLimitSeconds constants
    // (named `RateLimitSeconds` rather than `RateLimitWindow` because the
    // latter is already a DU type in ToolUp.Platform).
    [<RateLimit(30, RateLimitSeconds.perMinute)>]
    ExpensiveThing: Request -> Async<Result>

    // Multi-budget AND — short-burst + sustained caps compose.
    [<RateLimit(5, RateLimitSeconds.perSecond)>]
    [<RateLimit(1000, RateLimitSeconds.perHour)>]
    Hot: Request -> Async<Result>
}
```

Server-tier API records (those that can reference `ToolUp.Remoting.Server`) may use the upstream `RateLimitAttribute` + `RateLimitWindow` constants directly; the dispatcher's classifier recognises both families by simple type name and normalises them to the same budget.

Then compose a store at server build time:

```fsharp
ServerApp.create config
// ... existing composition ...
|> ServerApp.run   // wire `Remoting.withRateLimitStore` via your Api.make seam
```

## Conservative defaults shipped in forge

| Method | Budget |
|---|---|
| `AIAssistantApi.SubmitMessage` (LLM inference) | 30 / min |
| `FileManagementApi.UploadFile` | 30 / min |
| `KnowledgeApi.UploadDocument` (ingest) | 20 / min |
| `IPlatformKnowledgeApi.UploadPlatformDocument` | 20 / min |

Budgets are compile-time per-subject caps; tune by editing the annotation. They are dormant until you compose an `IRateLimitStore`.

## Verification

1. Compose an `InMemoryRateLimitStore`, then hammer an annotated method past its budget from one subject: the over-budget call returns `429` with a `Retry-After` header and an `ErrorCategory.RateLimit` envelope.
2. A second subject is unaffected (per-subject isolation — the security boundary).
3. With a bridged `IMetricsSink`: the denial records a `rate-limited` outcome tag distinct from `error`.
4. Inspect the audit tail: one `RemotingMethodAudited` row with kind `RateLimitExceeded`, the subject key, and `retryAfterSeconds`.
5. Contract pack: `InProcess/RateLimitTests.fs` in `ToolUp.Platform.Tests` (both attribute families, multi-budget AND, per-subject isolation, the annotation sweep, and the dispatcher-ordering + denial-emission source pins).

## Rollback

Remove the `[<RateLimit>]` annotations, or simply don't compose an `IRateLimitStore` (the annotations are inert without one). Revert forge commit `<this commit>` to drop the substrate wiring. Already-written `RateLimitExceeded` audit rows are ordinary audit events and need no cleanup.
