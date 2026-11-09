# 1068 — One in-memory rate-limit store, one admission decision, and a declared store posture

## What changes

**One store.** `ToolUp.Remoting.Server.InMemoryRateLimitStore` — the per-key, atomic, sliding store the remoting
dispatcher counts against — is the only in-memory rate-limit implementation. The Phase 56
`ToolUp.Platform.Infra.InMemoryRateLimitStore` is an `IncrementAndCheck` adapter over it (a calendar window is a
bucket per boundary; a `SlidingWindow` is the queue's own trailing window), and the Forms in-memory
`IShareTokenRateLimiter` keys it per `(scopeId, tokenId)`. With `RateLimitStore = InMemoryRateLimitStore`, the
composition root registers ONE store as `IRemotingRateLimitStore` and adapts it as `IRateLimitStore`.

Behaviour that moves with it:

- the Phase 56 in-memory store is bounded by the shared bucket cap (100 000 keys, oldest first); it was unbounded;
- its `SlidingWindow` now slides (it reset on every call, so it never refused anything);
- a denied call no longer counts against the window (a caller hammering a closed window does not extend it);
- the remoting store admits a non-positive budget on every call (it admitted the first call of each window and
  refused the rest). A non-positive Phase 56 threshold, and a non-positive share-token `MaxUses`, still refuse
  every call, as they always have.

**One decision.** Both store interfaces answer `Admission`:

```fsharp
[<RequireQualifiedAccess>]
type Admission =
    | Admit of remaining: int
    | Deny of retryAfter: TimeSpan
```

`IRateLimitStore.IncrementAndCheck` returns `Async<Result<Admission, RateLimitStoreError>>`;
`IRemotingRateLimitStore.TryAcquire` returns `Async<Admission>`. How a refusal looks to the caller (429, a held
request, a bare 204) is the route's `RateLimitOnExceeded`, never the store's. `RateLimitedError.ofDenial limit
window retryAfter` builds the typed 429 payload. The operator event log (`RateLimitDecisionEvent`) is unchanged.

**`DenySilently` is silent.** It answers a bare 204: no `RateLimit-*` and no `Retry-After` header (it stamped both,
telling the caller exactly what the policy says it must not learn).

**A declared store posture.** What a route does when its store cannot answer is `RouteLimit.StorePosture`:

```fsharp
[<RequireQualifiedAccess>]
type StorePosture =
    | FailOpen     // the default: admit and log — the behaviour before the posture existed
    | FailClosed   // refuse (503, or a bare 204 on a DenySilently route) and write a RateLimitRefused audit row
```

The live-session subscribe budget reads `LiveSessionOptions.SubscribeStorePosture` (default `FailOpen`). The
decision is taken once, by `RateLimitVerdict.ofResult`, not by each consumer. A share-token's limiter failing is
still refused (a token is a credential; that gate never fails open).

## Breaking changes and what a consumer does

| Removed / changed | Instead |
|---|---|
| `ToolUp.Remoting.Server.IRateLimitStore` | `IRemotingRateLimitStore` (same member, `TryAcquire`) |
| `RateLimitDecision` (`RateLimitAllowed` / `RateLimitDenied retryAfter`) | `Admission.Admit remaining` / `Admission.Deny retryAfter` |
| `IRateLimitStore.IncrementAndCheck` answers `InboundRateLimitDecision` | It answers `Admission`; a store that computes the old shape returns `InboundRateLimitDecision.toAdmission` of it |
| `RouteLimit` has five fields | It has six: a full literal adds `StorePosture = StorePosture.FailOpen` (or `FailClosed`) |
| `LiveSessionOptions` has four fields | It has five (`SubscribeStorePosture`); `LiveSessionOptions.defaults` sets it |

The codemod renames the remoting interface and reports the rest:

```powershell
dotnet run --project Build.fsproj -- Codemod <path-to-your-repo> --phase 1068
```

```diff
-let store = InMemoryRateLimitStore() :> ToolUp.Remoting.Server.IRateLimitStore
+let store = InMemoryRateLimitStore() :> ToolUp.Remoting.Server.IRemotingRateLimitStore
```

```diff
 match! store.IncrementAndCheck(key, PerMinute, 60) with
-| Ok(AllowWithRemaining remaining) -> ...
-| Ok(DenyWithError rle) -> ... rle.RetryAfterSeconds ...
+| Ok(Admission.Admit remaining) -> ...
+| Ok(Admission.Deny retryAfter) -> ... (RateLimitedError.ofDenial 60 PerMinute retryAfter).RetryAfterSeconds ...
```

A route whose budget is a security control declares `StorePosture = StorePosture.FailClosed`; nothing else changes
for a deployment that declares nothing.

## Verify

1. `dotnet build` your server.
2. A `DenySilently` route over budget answers 204 with no `RateLimit-*` / `Retry-After` header.
3. With a store that fails, a `FailOpen` route admits and logs; a `FailClosed` route answers 503 and an
   `IAuditLog` composed in DI receives a `RateLimitRefused` row.

## Rollback

Revert to the previous 0.x minor. There is no data to migrate: the in-memory store holds nothing across restarts,
and the external stores' keys and windows are unchanged.
