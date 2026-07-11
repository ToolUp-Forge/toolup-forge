module ToolUp.Platform.Tests.InProcess.IdempotencyStoreEvictionTests

open System
open System.Collections.Concurrent
open System.Reflection
open Expecto
open ToolUp.Remoting.Server

// ─── Phase 328 — bounded idempotency-store eviction + observability ──────
//
// `InMemoryIdempotencyStore.evictOldestIfFull` used to call `entries.Clear()`
// on a recovery branch: under a count/queue race at the `maxEntries` cap
// (a concurrent inserter has bumped `entries.Count` but not yet enqueued its
// key, so the FIFO queue is momentarily empty while the dictionary is over
// cap) that wiped the ENTIRE memoised-response cache. Every in-flight
// idempotency key then missed and re-executed its handler — a double-charge /
// duplicate side effect, silently, and only under cap pressure. This pack
// proves the mass wipe is gone:
//
//   * the normal at-cap path is a BOUNDED FIFO drain — one oldest entry out
//     per Store, never the whole cache;
//   * the recovery branch accepts a transient slight over-cap instead of
//     clearing, so previously-stored keys still resolve to their memoised
//     response (no re-execution);
//   * the recovery path is OBSERVABLE — an incremented `OverCapRecoveryCount`
//     counter plus a `Warn` on the injected logger.

// ── Fixtures ────────────────────────────────────────────────────────

let private ttl = TimeSpan.FromHours 1.0

let private resp (n: int) : MemoisedResponse = {
    Body = [| byte n |]
    StatusCode = 200
    ContentType = "application/json"
    RequestBodyHash = ""
}

/// Captures every `Warn` line so the recovery-path signal is assertable.
type private CapturingLogger() =
    let warns = ConcurrentQueue<string>()
    member _.Warns = warns

    interface ToolUp.Platform.ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn m = warns.Enqueue m
        member _.Error(_, _) = ()

/// The count/queue race is timing-dependent through the public API, so the
/// deterministic recovery test reproduces it white-box: empty the internal
/// FIFO queue while the dictionary stays full. The queue is the sole
/// `ConcurrentQueue<string>` field on the store — find it by type so the
/// test is robust to F# field-name mangling.
let private emptyInternalOrderQueue (store: InMemoryIdempotencyStore) =
    let field =
        typeof<InMemoryIdempotencyStore>.GetFields(BindingFlags.NonPublic ||| BindingFlags.Instance)
        |> Array.find (fun f -> typeof<ConcurrentQueue<string>>.IsAssignableFrom f.FieldType)

    (field.GetValue store :?> ConcurrentQueue<string>).Clear()

// Sequenced (not run in parallel with the rest of the pack). The concurrency
// smoke case below drives an 8-worker `Async.Parallel` insert storm.
//
// History: this case originally flaked (`final-k0 ... Expected Some, was None`)
// and was wrongly attributed to thread-pool contention — `testSequenced` was
// added as a band-aid on that theory. The real cause was a store defect: the
// eviction bookkeeping spanned `entries` + `order` non-atomically, so under
// contention `compactStaleHeads` (peek-head → decide-stale → dequeue) could
// discard a *live* key's FIFO marker when a concurrent evictor removed the
// peeked head first. That orphaned the entry (present in `entries`, absent from
// `order`) — unevictable, so it squatted a cap slot and forced later inserts to
// evict the *newest* keys, dropping the freshly-stored `final-k*` batch. Fixed
// by serialising the store's write path (`InMemoryIdempotencyStore`'s `gate`).
// Sequencing is retained only because an 8×500 insert storm is a heavy neighbour
// for other timing-sensitive cases — it is no longer masking a real defect.
[<Tests>]
let tests =
    testSequenced
    <| testList "Phase 328 — idempotency-store bounded eviction" [

        // ── Bounded FIFO drain: one oldest out per Store, never a wipe ──
        testCaseAsync "bounded FIFO drain evicts only the oldest per Store — never a mass wipe"
        <| async {
            let cap = 4
            let store = InMemoryIdempotencyStore(maxEntries = cap)
            let istore = store :> IIdempotencyStore

            // Fill exactly to the cap: k0..k3.
            for i in 0 .. cap - 1 do
                do! istore.Store(sprintf "k%d" i, "s", resp i, ttl)

            Expect.equal store.Count cap "store is full at the cap"

            // Each further Store evicts exactly the OLDEST entry (FIFO),
            // never the whole cache. After k4, k0 is gone but k1..k4 remain.
            do! istore.Store("k4", "s", resp 4, ttl)
            Expect.equal store.Count cap "still exactly at cap — one in, one out"

            let! g0 = istore.TryGet("k0", "s")
            Expect.isNone g0 "the oldest key was evicted (FIFO)"

            for i in 1..4 do
                let! g = istore.TryGet(sprintf "k%d" i, "s")
                Expect.isSome g (sprintf "k%d survived — only the oldest was dropped, not the cache" i)

            Expect.equal store.OverCapRecoveryCount 0L "the normal at-cap path is not a recovery event"
        }

        // ── The replaced branch: recovery, not mass wipe (deterministic) ──
        testCaseAsync "over-cap recovery keeps the cache and fires the Warn + counter (no entries.Clear)"
        <| async {
            let cap = 8
            let logger = CapturingLogger()
            let store = InMemoryIdempotencyStore(maxEntries = cap, logger = logger)
            let istore = store :> IIdempotencyStore

            for i in 0 .. cap - 1 do
                do! istore.Store(sprintf "k%d" i, "s", resp i, ttl)

            Expect.equal store.Count cap "full at the cap before the induced race"

            // Reproduce the count/queue race deterministically — empty the
            // FIFO queue while the dictionary stays full, so the next Store's
            // eviction finds entries.Count >= cap but the queue empty (exactly
            // the window a concurrent inserter opens between `entries[k] <- …`
            // and `order.Enqueue k`). Pre-328 this branch called
            // `entries.Clear()` and wiped every memoised response.
            emptyInternalOrderQueue store

            // The Store whose eviction hits the empty-queue recovery branch.
            do! istore.Store("k-new", "s", resp 99, ttl)

            // No wipe: every one of the cap original keys still replays.
            for i in 0 .. cap - 1 do
                let! g = istore.TryGet(sprintf "k%d" i, "s")
                Expect.isSome g (sprintf "k%d still resolves — the recovery path did NOT clear the cache" i)

            // The inserting key landed too (transient slight over-cap accepted).
            let! gNew = istore.TryGet("k-new", "s")
            Expect.isSome gNew "the inserting key was stored; a transient over-cap is accepted"
            Expect.isGreaterThan store.Count cap "the store is transiently over cap, not wiped to near-zero"

            // Observability: the recovery path is visible to an operator.
            Expect.equal store.OverCapRecoveryCount 1L "the over-cap recovery counter incremented once"
            Expect.equal logger.Warns.Count 1 "exactly one Warn was emitted on the recovery path"
            let warn = logger.Warns |> Seq.head
            Expect.stringContains warn "over-cap" "the Warn names the over-cap recovery"
        }

        // ── Concurrency smoke: no wipe, no crash, observability consistent ──
        testCaseAsync "concurrent inserts at the cap never collapse the cache; counter/logger stay consistent"
        <| async {
            let cap = 64
            let logger = CapturingLogger()
            let store = InMemoryIdempotencyStore(maxEntries = cap, logger = logger)
            let istore = store :> IIdempotencyStore

            // Hammer the store far past the cap from many threads. Whether or
            // not the count/queue race actually fires (it is timing-dependent),
            // the invariants below hold.
            let workers = 8
            let perWorker = 500

            do!
                [
                    for w in 0 .. workers - 1 ->
                        async {
                            for i in 0 .. perWorker - 1 do
                                do! istore.Store(sprintf "w%d-k%d" w i, "s", resp i, ttl)
                        }
                ]
                |> Async.Parallel
                |> Async.Ignore

            // Saturated near the cap — NOT emptied by a mass wipe (pre-328 a
            // fired race could Clear the whole cache), and bounded above.
            Expect.isGreaterThan store.Count (cap / 2) "the store stays full — no mass wipe collapsed it"
            Expect.isLessThan store.Count (cap * 3) "over-cap is bounded to a small transient slack"

            // The observability wiring is consistent regardless of whether the
            // race fired: exactly one Warn per counted recovery.
            Expect.equal
                (int64 logger.Warns.Count)
                store.OverCapRecoveryCount
                "each over-cap recovery emitted exactly one Warn"

            // Now the storm is over, a sequential final batch of `cap` fresh
            // keys is retained in full — the newest `cap` insertions always
            // survive FIFO eviction (during the storm, any single key can be
            // pushed out by later inserts from other workers, so a specific
            // in-storm key is not a valid invariant — a mass wipe, which this
            // asserts against, is).
            for i in 0 .. cap - 1 do
                do! istore.Store(sprintf "final-k%d" i, "s", resp i, ttl)

            for i in 0 .. cap - 1 do
                let! g = istore.TryGet(sprintf "final-k%d" i, "s")
                Expect.isSome g (sprintf "final-k%d resolves — the newest cap keys are never lost" i)
        }
    ]