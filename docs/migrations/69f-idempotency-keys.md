# Phase 69f — idempotency keys on mutating methods

> **Substrate status: shipped, with one deferred companion.** The `[<Idempotent>]` attribute, the `IIdempotencyStore` seam, the bounded `InMemoryIdempotencyStore` default, and the dispatcher enforcement (including request-body conflict detection) are all live (`Server/Remoting/Idempotency.fs` + `Types.fs`). A blob-backed distributed store is a planned follow-up; until it lands, multi-instance deployments implement `IIdempotencyStore` themselves (two methods).

## What changes

A POST-shaped method retried by a network retry loop, a job runner, or a flaky client produces *two domain mutations*. 69f brings the standard fix into the transport: the client sends an `X-Idempotency-Key` header; the server memoises the first response and **replays it without invoking the handler** on subsequent calls with the same key, within a TTL (default 1 hour).

Two opt-ins, both required before anything changes (GP 11):

1. **Per method** — mark it `[<Idempotent>]` on the API record field. Without a composed store the attribute is dormant (the method runs normally on every call).
2. **Per deployment** — compose a store via `Remoting.withIdempotencyStore`. With a store composed, calls to `[<Idempotent>]` methods **require** the header; a missing header is refused with a `User`-categorised envelope.

Cache slots are keyed `{subjectId}|{methodName}|{key}` — the same UUID submitted by two different subjects is two distinct slots (a security boundary, not a sharing one). The dispatcher also stamps a SHA-256 of the request body into the memo: reusing a key with a **different** body surfaces as a `User`-categorised envelope + `409 Conflict` rather than silently replaying the prior response.

In the pre-flight chain, idempotency runs after auth and **before** rate-limit, so a replay isn't double-charged against the rate budget.

## Diff to apply

```fsharp
// API record — mark the mutating method:
type OrdersApi = {
    [<Idempotent>]
    PlaceOrder: PlaceOrderRequest -> Async<OrderReceipt>
    GetOrder: string -> Async<OrderReceipt option>   // unmarked — header ignored
}

// Composition — register a store (in-memory default, bounded at 100k entries):
Remoting.createApi ()
|> Remoting.withIdempotencyStore (InMemoryIdempotencyStore())
|> Remoting.withIdempotencyTtl (TimeSpan.FromMinutes 30.)   // optional; default 1h
|> Remoting.fromValue ordersApi
```

Clients attach the header per call (one UUID per logical operation, reused across retries of that operation). The replayed response carries the original response bytes, so clients need no special handling.

`InMemoryIdempotencyStore` is single-instance and restart-volatile — right for dev and single-node deployments. For multi-instance, implement `IIdempotencyStore` (`TryGet` / `Store`, both async, string-keyed — the interface honours the six portability rules) over Redis / a database / blob storage.

## Verification

1. `dotnet build` — clean.
2. First call with header `K`: handler runs, response returned, normal audit/telemetry.
3. Second call with header `K`, same body, same subject: identical response bytes, handler **not** invoked.
4. Same header `K` but a different body: `409 Conflict` with a `User`-categorised envelope.
5. Call to an `[<Idempotent>]` method with **no** header (store composed): refused with a `User`-categorised envelope.
6. Unmarked methods ignore the header entirely.

## Rollback

Remove the `withIdempotencyStore` line — every `[<Idempotent>]` attribute goes dormant and the deployment reverts to pre-69f behaviour byte-for-byte. The attributes themselves can stay in place for a later re-enable.

## See also

- [69-family-overview.md](69-family-overview.md) — pre-flight chain ordering (auth → idempotency → rate-limit).
- [69i-long-running-handle.md](69i-long-running-handle.md) — the planned key→job-id dedup integration for long-running methods.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/Idempotency.fs`.
