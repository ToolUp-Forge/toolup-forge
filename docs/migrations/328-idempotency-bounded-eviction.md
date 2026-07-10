# Phase 328 — Bounded idempotency-store eviction + mass-eviction observability

**What changes.** `InMemoryIdempotencyStore` (the default in-process
`IIdempotencyStore` for ToolUp.Remoting `[<Idempotent>]` methods) no longer
mass-wipes its memoised-response cache under cap pressure. Its
`evictOldestIfFull` recovery branch used to call `entries.Clear()`; under a
count/queue race at the `maxEntries` cap that discarded **every** memoised
response, so every in-flight idempotency key missed and re-executed its
handler. It is replaced by a **bounded FIFO drain** that accepts a transient
slight over-cap instead of clearing, plus an **observable recovery signal**.

**No action is required for the default.** The change is behaviour-preserving
for the correct path (GP 11): a deployment that never hits the cap race is
byte-for-byte identical. There is no wire change and no compose change unless
you want the new observability signal (below).

**Scope.** Server-side in-process store only. No wire change. No consumer
source edit required.

## Why it mattered

`[<Idempotent>]` exists to guarantee at-most-once *effect* for retried calls
(payments, provisioning, any non-idempotent side effect). The old recovery
branch fired precisely under cap pressure — i.e. high traffic — and, when the
FIFO queue was momentarily empty while the dictionary was still at/over cap
(a concurrent inserter had bumped `entries.Count` between `entries[k] <- …`
and `order.Enqueue k`), it cleared the whole cache. Every key then missed and
re-ran its handler: a double-charge / duplicate side effect, silently, with no
log line. The fix removes the mass wipe and makes the (rare) recovery visible.

## New behaviour

- **Bounded FIFO drain.** Each `Store` evicts at most a fixed batch of oldest
  entries (steady state: one in, one out at the cap). A concurrent-insert
  burst that pushes past the cap leaves a transient slight over-cap that the
  next `Store` continues to drain — no single call spins unboundedly, and the
  cache is never cleared.
- **Observable recovery path.** When the FIFO queue is empty while the
  dictionary is at/over cap, the store increments a counter and (if a logger
  is wired) emits a `Warn` instead of clearing:
  - `member InMemoryIdempotencyStore.OverCapRecoveryCount : int64` — cumulative
    over-cap recovery events. A non-zero and climbing value means this instance
    is under cap pressure; raise `maxEntries` or move to a distributed store.
  - An optional constructor logger emits a `Warn` naming the over-cap recovery.

## Opting into the observability signal

The constructor gained an optional `logger` parameter (additive; the prior
`InMemoryIdempotencyStore()` / `InMemoryIdempotencyStore(cap)` call shapes
still compile and stay silent):

```fsharp
open ToolUp.Remoting.Server

// `logger` is your already-composed ToolUp.Platform.ILogger.
let store = InMemoryIdempotencyStore(maxEntries = 100_000, logger = logger)
let options = withIdempotencyStore (store :> IIdempotencyStore) options

// Poll the counter from a health check / metrics probe:
// store.OverCapRecoveryCount
```

Omit `logger` to keep the pre-328 silent behaviour; the counter is still
available for polling either way.

## When you need more

`OverCapRecoveryCount` climbing steadily is the signal that per-instance memory
is the wrong home for this deployment's idempotency state (it is also lost on
restart and not shared across replicas). Wire a distributed `IIdempotencyStore`
— the shipped `BlobIdempotencyStore` over `IBlobStorage`, or a
compare-and-set backend (Redis `SETNX` / DynamoDB conditional put) for stricter
once-only handler invocation — against the same interface contract.

## Verification

- `dotnet run --project Build.fsproj -- VerifyAll` — the `Platform` pack's
  `Phase 328 — idempotency-store bounded eviction` suite proves: the at-cap
  path evicts only the oldest entry (never the whole cache); the induced
  count/queue race keeps every previously-stored key resolvable and fires the
  `Warn` + counter exactly once; and concurrent inserts far past the cap never
  collapse the cache, with the Warn count matching `OverCapRecoveryCount`.
- The `IIdempotencyStoreContract` conformance pack continues to pass against
  `InMemoryIdempotencyStore` unchanged (correct-path behaviour preserved).

## Rollback

Behaviour-preserving on the correct path, so no rollback is normally needed.
To drop the observability signal, remove the `logger` argument from the
constructor — the store returns to its silent (but still bounded, never
mass-wiping) behaviour.
