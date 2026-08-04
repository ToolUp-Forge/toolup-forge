// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Metrics

// ─── Phase 485 — memoizing the terminal outcome of external work ─────────
//
// Re-submitting work that has already been done re-runs it. Phase 318's
// seam says a backend SHOULD return the existing handle for a repeated
// idempotency key, but that is a *backend* promise about a window the
// backend chooses, and nothing above the seam retains anything: a handler
// that submits the same fit twice a day apart pays for it twice, and a
// budget decorator ([Phase 451](451-compute-budget-governance.md)) charges
// for both. This decorator is the platform-side answer — one cache, keyed
// on what the work IS rather than on which handle it produced.
//
// **Two windows, two mechanisms, and they are not the same thing.**
//
//   1. **Outcome memoization** covers the duplicate that arrives AFTER the
//      first submission finished. The terminal `Succeeded` outcome is
//      cached against the spec's key; a later identical submit returns the
//      stored handle without touching the backend.
//   2. **Submit coalescing** covers the duplicate that arrives WHILE the
//      first is still running. There is no terminal outcome to serve yet —
//      only `Succeeded` is ever cached, and a running job has succeeded at
//      nothing — so the second caller cannot be answered from the cache. It
//      is instead joined to the first caller's in-flight `Submit` and
//      handed the SAME handle. Both then poll that one handle to the one
//      outcome the one execution produces.
//
// Conflating the two is the trap: a cache alone leaves a concurrent burst
// dispatching N times (each missing, because none has finished), and a
// coalescer alone forgets everything the moment the submit returns. The
// acceptance criterion names both, and they are enforced by different code
// paths below.
//
// **Only an idempotency-keyed spec is memoizable, and that is a safety
// property rather than a convenience.** `ExternalWorkSpec.Idempotency` is
// caller-minted and optional, so its presence is an explicit per-submission
// assertion that re-submitting this exact work is the same request rather
// than a second one. Work without it is passed straight through and never
// cached: forge cannot tell "render this scene again because the first
// output was fine" from "charge this card again", and a cache that guessed
// would silently deduplicate side effects. `None` is therefore the
// no-memoization case by construction, not by policy (GP 13).
//
// **Only `Succeeded` caches.** A `Failed` outcome is frequently transient
// (`ExternalComputeError.Retriable` says so as data) and caching it would
// convert a retriable blip into a TTL-long outage for that spec; a
// `Cancelled` outcome is a decision about one submission, not a fact about
// the work. Both are passed through and leave the cache empty, so the next
// submission genuinely re-dispatches.
//
// **The cache key extends the phase's four-tuple with the execution
// profile, deliberately.** The stated key is `(scopeId, Kind,
// SHA-256(payload), Idempotency)`. `ExecutionProfile` is added because the
// idempotency key is caller-minted and nothing stops one being reused
// across profiles: without the profile in the key, an
// `ExecutionProfile.Isolated` submission could be served a result computed
// on a backend that never declared the isolation posture — precisely the
// leak Phase 478 exists to prevent, arriving through a cache rather than
// through a route. With it, the two are different entries and an
// `Isolated` hit can only replay work an isolating backend actually ran
// (only `Succeeded` caches, and the Phase 478 gate refuses before any
// backend runs). The residual bound is that a backend downgraded since the
// entry was written is not re-checked; the TTL is what bounds that, and it
// is why the default TTL is an hour rather than a week.
//
// **Scope partitioning is structural (GP 4), and checked twice.** `scopeId`
// is in the in-memory key AND in the blob path
// (`compute-memo/{scopeId}/{digest}.json`, the layout
// `BlobBackedBackgroundExportStore` uses), so a lookup for one scope never
// constructs a path under another. The stored envelope additionally repeats
// the whole key and a read whose envelope does not match the key it was
// looked up under is treated as a miss — so a mis-derived path or a digest
// collision degrades to a re-dispatch rather than to one tenant reading
// another's result.
//
// **What is durable and what is not — stated because the difference is the
// interesting part.** The SUBMIT path is durable: the entry is a blob, so
// an identical submission after a restart is a hit and the work is not
// re-run. `Poll`'s short-circuit is an in-process optimisation over the
// in-memory handle index only — it takes no blob read, because a poll of a
// handle this process did not submit is overwhelmingly the common case
// (Phase 319's reconciliation polls handles read back from the job store)
// and a blob read per poll would be a real cost paid for a rare hit. A poll
// that misses the index falls through to the backend exactly as an
// undecorated deployment would, which is a slower answer and never a wrong
// one. The same asymmetry means a restart BETWEEN a submit and its
// completion loses that item's memo entry: the work still completes and the
// caller still gets its outcome from the backend, and the next identical
// submission re-dispatches. Failing by omission is the only failure
// direction a cache may have.
//
// **Composition order: memo OUTSIDE budget.** With
// [Phase 451](451-compute-budget-governance.md) — unshipped at the time of
// writing, so this is the order it must be composed in when it lands —
// `MemoizedComputeDispatcher(BudgetedComputeDispatcher(backend))`. A hit
// returns before the inner dispatcher is consulted at all, so it spends no
// allowance and consumes no concurrency slot, which is the whole of "a
// memoized hit costs zero budget". The reverse order (budget outside memo)
// charges for every hit and is simply the feature not working.
//
// **This decorator does NOT implement `IIsolatedComputeBackend`, and must
// not.** It is not a backend and has no posture of its own. Re-declaring
// the inner posture — which Phase 478's `ExecutionProfileGate.enforce`
// correctly does — would be wrong here in both directions: over a plain
// backend it would claim a guarantee about work the memo may answer without
// any backend running, and over Phase 484's `RoutingComputeDispatcher`
// (which deliberately declares nothing, so that routing can filter per
// spec) it would read as `standardOnly` and flip every `Isolated`
// submission from correctly-routed to refused. Claiming nothing is Phase
// 478's honest reading for something that is not a backend. Compose the
// profile gate INSIDE the memo (`memoize (enforce backend)`) or leave it to
// the router.
//
// **Bounded, and the bound is observable (Phase 328's discipline).** Both
// in-memory indexes are capped and drain FIFO in bounded batches; a
// count/queue race at the cap accepts a transient slight over-cap and
// records the recovery rather than clearing the cache. The reason is the
// one Phase 328 learned the expensive way on the idempotency store: a mass
// wipe under cap pressure is silent, and every entry it discards becomes a
// re-dispatch of work that was already paid for. The blob mirror is bounded
// by TTL instead — an expired entry is deleted on the read that finds it —
// so a deployment with a high unique-spec churn wants a sweep job for the
// residue, the same follow-up `BlobBackedBackgroundExportStore` defers.

/// Phase 485 — the identity of one unit of memoizable work.
///
/// `(scopeId, Kind, SHA-256(payload), Idempotency)` plus the execution
/// profile — see the file header for why the profile is in the key. Every
/// field is a string so the key serialises, survives a restart, and hashes
/// to a blob-safe digest (GP 12 rule 1).
type ComputeMemoKey = {
    /// Scope the work was submitted under. In the key AND in the blob path
    /// — a hit never crosses a scope (GP 4).
    ScopeId: string
    /// The spec's backend-resolved work discriminator.
    Kind: string
    /// SHA-256 (lowercase hex) of the spec's payload bytes. The payload
    /// itself is opaque to the platform and is never parsed or normalised,
    /// so byte equality is the only equality available — a semantically
    /// identical payload with different whitespace is a different entry,
    /// which is the conservative direction.
    PayloadHash: string
    /// The caller-minted idempotency key. Its presence is what makes a
    /// spec memoizable at all.
    IdempotencyKey: string
    /// `ExecutionProfile.label` of the spec's profile.
    Profile: string
}

[<RequireQualifiedAccess>]
module ComputeMemoKey =

    let private sha256Hex (bytes: byte[]) : string =
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()

    /// SHA-256 (lowercase hex) of a payload string, as `PayloadHash`.
    let hashPayload (payload: string) : string =
        sha256Hex (Text.Encoding.UTF8.GetBytes(payload: string))

    /// The key for `spec` under `scopeId`, or `None` when the spec is not
    /// memoizable.
    ///
    /// `None` for a spec with no idempotency key (or a blank one — a
    /// whitespace key is a caller defect, and treating it as an opt-in
    /// would memoize on an empty assertion). A `None` here is the whole of
    /// the opt-in: the decorator's `Submit` passes such a spec through
    /// untouched.
    let forSpec (scopeId: string) (spec: ExternalWorkSpec) : ComputeMemoKey option =
        match spec.Idempotency with
        | Some key when not (String.IsNullOrWhiteSpace key) ->
            Some {
                ScopeId = scopeId
                Kind = spec.Kind
                PayloadHash = hashPayload spec.Payload
                IdempotencyKey = key
                Profile = ExecutionProfile.label spec.Profile
            }
        | _ -> None

    /// The in-memory dictionary key: every field, `|`-joined. Not a hash —
    /// two distinct keys must never collide in the in-process index, and
    /// the field values are already bounded strings.
    let composite (key: ComputeMemoKey) : string =
        String.Join("|", [| key.ScopeId; key.Kind; key.PayloadHash; key.IdempotencyKey; key.Profile |])

    /// Blob-name-safe digest of the whole key. Hashed rather than embedded
    /// because `Kind` and the idempotency key are caller-supplied and may
    /// carry anything, and a blob name is not the place to discover that.
    let digest (key: ComputeMemoKey) : string =
        sha256Hex (Text.Encoding.UTF8.GetBytes(composite key))

/// Phase 485 — where a memo entry lives. Shared so a future sweep job
/// enumerates exactly what the decorator writes, the reason
/// `BlobIdempotencyLayout` exists.
[<RequireQualifiedAccess>]
module ComputeMemoLayout =
    /// Reserved SDK-level container the entries live under.
    [<Literal>]
    let DefaultContainer = "_platform"

    /// Blob-name prefix shared by every memo entry (the `List` argument a
    /// sweep would enumerate).
    [<Literal>]
    let BlobPrefix = "compute-memo/"

    /// Every entry for one scope. The scope segment is what makes the
    /// layout structurally isolating (GP 4) — the prefix a caller can
    /// enumerate is bounded by the scope it resolved.
    let scopePrefix (scopeId: string) : string = BlobPrefix + scopeId + "/"

    /// The entry blob for one key's digest under one scope.
    let entryBlob (scopeId: string) (digest: string) : string = scopePrefix scopeId + digest + ".json"

/// Phase 485 — one cached terminal outcome.
///
/// Holds the ORIGINAL handle, unchanged. A hit returns it verbatim, so a
/// caller cannot distinguish a memo hit from Phase 318's own "return the
/// existing handle" idempotent-submit behaviour — which is the point: the
/// decorator adds a guarantee, not a new shape to code against.
type ComputeMemoEntry = {
    /// The handle the first submission returned.
    Handle: ExternalHandle
    /// The opaque backend result reference from the `Succeeded` outcome.
    /// Echoed, never dereferenced or parsed.
    ResultRef: string
    /// When the outcome was recorded (UTC).
    CachedAt: DateTime
    /// When the entry stops being served (UTC). Absolute rather than a
    /// duration so it survives serialisation and a restart without the
    /// reader needing to know the TTL the writer used.
    ExpiresAt: DateTime
}

/// Phase 485 — what the memo has done. The read model behind diagnostics
/// and the assertions a test can make about *why* something was fast.
type ComputeMemoStats = {
    /// Submissions answered from a cached terminal outcome, with no
    /// dispatch.
    Hits: int64
    /// Submissions that found no live entry.
    Misses: int64
    /// Misses that were joined to an already-in-flight identical submit
    /// rather than dispatching their own. A subset of `Misses`.
    Coalesced: int64
    /// Terminal `Succeeded` outcomes written to the cache.
    Stored: int64
    /// Entries found past their TTL on read and discarded.
    Expired: int64
    /// Polls answered from the in-memory index without reaching the
    /// backend.
    PollShortCircuits: int64
    /// Entries dropped by the FIFO cap.
    Evicted: int64
    /// Phase 328's signal — times a transient over-cap was accepted
    /// rather than discarding live entries. A climbing value means the cap
    /// is under-provisioned for this instance's churn.
    OverCapRecoveries: int64
    /// Live in-memory entries.
    Entries: int
    /// The configured cap.
    MaxEntries: int
}

/// Phase 485 — metric names the memo emits. Declared here for the reason
/// `ComputeFleetMetrics` is: the standard-metrics module compiles later.
///
/// Tagged `kind` only. `scope` is deliberately NOT a tag: scope
/// cardinality is unbounded in a multi-tenant deployment and a per-scope
/// time series is how a metrics backend gets billed into being switched
/// off.
[<RequireQualifiedAccess>]
module ComputeMemoMetrics =
    /// Counter — submissions served from the cache, tagged `kind`.
    let HitsTotal = "toolup.compute.memo.hits.total"

    /// Counter — submissions that found no live entry, tagged `kind`.
    let MissesTotal = "toolup.compute.memo.misses.total"

    /// Counter — submissions joined to an in-flight identical submit,
    /// tagged `kind`.
    let CoalescedTotal = "toolup.compute.memo.coalesced.total"

    /// Counter — entries dropped by the cap, untagged (the victim's kind
    /// is no longer in hand at eviction time, and inventing one would be
    /// worse than omitting it).
    let EvictionsTotal = "toolup.compute.memo.evictions.total"

    /// Gauge — live in-memory entries.
    let Entries = "toolup.compute.memo.entries"

/// Phase 485 — a bounded, FIFO-draining in-memory index. Phase 328's
/// discipline, extracted so both of the decorator's indexes get it:
/// eviction is a bounded drain of the oldest keys, and the cap-race branch
/// accepts a transient over-cap and reports it instead of wiping.
///
/// `internal` because it is the decorator's innards, not a surface a
/// consumer composes against — the same posture `ComputeBackendCounters`
/// takes.
type internal BoundedMemoIndex<'K, 'V when 'K: equality>(cap: int, recordOverCap: unit -> unit) =

    // Bound a single `Set`'s eviction work. One `Set` adds one entry, so
    // steady state removes one victim; the batch caps the catch-up drain
    // after a concurrent burst. A residue is fine — the next `Set`
    // continues it.
    let drainBatch = 64

    let entries = ConcurrentDictionary<'K, 'V>()
    let order = ConcurrentQueue<'K>()

    // Single-writer gate over `order`, for the reason Phase 328 documents
    // at length on `InMemoryIdempotencyStore`: the read-modify-write across
    // the dictionary and the queue is not atomic on the lock-free
    // primitives, and a lost FIFO marker orphans a live entry into an
    // unevictable cap squatter.
    let gate = obj ()

    let evictions: int64[] = Array.zeroCreate 1

    /// Drop heads whose entry is already gone (lazy TTL removal leaves a
    /// stale marker). Staleness is decided against the value actually
    /// dequeued, and a live marker pulled by a racing head advance is
    /// re-enqueued rather than orphaned.
    let compactStaleHeads () =
        let mutable keepCompacting = true

        while keepCompacting do
            let mutable peeked = Unchecked.defaultof<'K>

            if order.TryPeek(&peeked) && not (entries.ContainsKey peeked) then
                let mutable dequeued = Unchecked.defaultof<'K>

                if order.TryDequeue(&dequeued) then
                    if entries.ContainsKey dequeued then
                        order.Enqueue dequeued
                        keepCompacting <- false
                else
                    keepCompacting <- false
            else
                keepCompacting <- false

    let evictOldestIfFull () =
        compactStaleHeads ()

        let mutable drained = 0
        let mutable keepDraining = true

        while keepDraining && entries.Count >= cap do
            let mutable victim = Unchecked.defaultof<'K>

            if order.TryDequeue(&victim) then
                entries.TryRemove victim |> ignore
                Interlocked.Increment(&evictions[0]) |> ignore
                drained <- drained + 1

                if drained >= drainBatch then
                    keepDraining <- false
            else
                // Cap-race: at/over cap with a momentarily empty queue.
                // Accept the transient over-cap and report it; NEVER wipe.
                keepDraining <- false
                recordOverCap ()

    member _.Count = entries.Count
    member _.MaxEntries = cap

    member _.Evictions = Interlocked.Read(&evictions[0])

    member _.TryGet(key: 'K) : 'V option =
        match entries.TryGetValue key with
        | true, value -> Some value
        | false, _ -> None

    member _.Set(key: 'K, value: 'V) =
        lock gate (fun () ->
            evictOldestIfFull ()
            entries[key] <- value
            order.Enqueue key)

    member _.Remove(key: 'K) = entries.TryRemove key |> ignore

/// Phase 485 — the memo's defaults, and the one composition rule that
/// matters. Constants rather than magic numbers at the call site, so a
/// deployment overriding them is visibly making a choice.
[<RequireQualifiedAccess>]
module ComputeMemoization =
    /// How long a cached outcome is served. An hour: long enough that a
    /// re-submission inside one workflow (or one operator's retry storm)
    /// is free, short enough to bound every staleness argument the file
    /// header raises — a backend whose isolation posture or capability set
    /// changed is re-consulted within the hour rather than at the end of a
    /// week.
    let defaultTtl = TimeSpan.FromHours 1.0

    /// In-memory entry cap per index. Bounded so a churn of unique
    /// idempotency keys cannot grow the process; the blob mirror is
    /// bounded by TTL instead.
    let defaultMaxEntries = 10_000

/// Phase 485 — an `IExternalComputeDispatcher` that returns the cached
/// terminal outcome of an identical idempotent submission instead of
/// dispatching it again.
///
/// Wraps ANY dispatcher — a companion backend, Phase 478's profile gate,
/// Phase 484's router, Phase 451's budget decorator. Compose it
/// **outermost** so a hit is free: see the file header's composition-order
/// note.
///
/// `blobs` is optional. Supplied, entries survive a restart; omitted, the
/// memo is in-process only and a restart is a cold cache — still correct,
/// just not durable. `logger` and `metrics` are optional for the reason
/// Phase 328's store takes an optional logger: omitting them is silent and
/// behaviour-preserving (GP 11). `clock` exists so TTL expiry is testable
/// without sleeping; it defaults to `DateTime.UtcNow`.
type MemoizedComputeDispatcher
    (
        inner: IExternalComputeDispatcher,
        ?ttl: TimeSpan,
        ?maxEntries: int,
        ?blobs: IBlobStorage,
        ?container: string,
        ?logger: ILogger,
        ?metrics: IMetricsSink,
        ?clock: unit -> DateTime
    ) =

    let ttl = defaultArg ttl ComputeMemoization.defaultTtl
    let cap = defaultArg maxEntries ComputeMemoization.defaultMaxEntries
    let container = defaultArg container ComputeMemoLayout.DefaultContainer
    let now = defaultArg clock (fun () -> DateTime.UtcNow)

    // Counters are heap arrays rather than `let mutable` fields for the
    // reason Phase 328 records: `Interlocked` needs a byref, and a mutable
    // field cannot be address-taken from inside a closure — which every
    // increment below is, sitting in an `async` block.
    let hits: int64[] = Array.zeroCreate 1
    let misses: int64[] = Array.zeroCreate 1
    let coalesced: int64[] = Array.zeroCreate 1
    let stored: int64[] = Array.zeroCreate 1
    let expired: int64[] = Array.zeroCreate 1
    let pollShortCircuits: int64[] = Array.zeroCreate 1
    let overCapRecoveries: int64[] = Array.zeroCreate 1

    let recordOverCap () =
        let total = Interlocked.Increment(&overCapRecoveries[0])

        match logger with
        | Some log ->
            log.Warn(
                sprintf
                    "MemoizedComputeDispatcher: FIFO eviction queue empty while at/over the %d-entry cap — accepting a transient over-cap instead of clearing the cache (cumulative over-cap recoveries: %d). Sustained occurrences mean the memo cap is under-provisioned for this instance's unique-spec churn; raise maxEntries."
                    cap
                    total
            )
        | None -> ()

    /// Cached terminal outcomes, keyed by `ComputeMemoKey.composite`.
    let entries = BoundedMemoIndex<string, ComputeMemoEntry>(cap, recordOverCap)

    /// Handle → key, so `Poll` can find the entry for a handle this
    /// process submitted. Written on every memoizable submission (hit or
    /// miss), which makes it also the record that lets a later `Poll` of a
    /// miss STORE its outcome.
    let byHandle = BoundedMemoIndex<Guid, ComputeMemoKey>(cap, recordOverCap)

    /// In-flight submits, keyed by composite. The coalescing window: a
    /// duplicate arriving while the first `Submit` is outstanding joins the
    /// same `Task` instead of issuing its own. Unbounded by design — it is
    /// bounded by concurrent outstanding submissions, and the leader
    /// removes its entry when the submit returns.
    let inFlight =
        ConcurrentDictionary<string, Lazy<Task<Result<ExternalHandle, ExternalComputeError>>>>()

    let increment (counter: int64[]) =
        Interlocked.Increment(&counter[0]) |> ignore

    let emitCount (name: string) (kind: string) =
        match metrics with
        | Some sink -> sink.Increment(name, Map [ "kind", kind ])
        | None -> ()

    let emitEntriesGauge () =
        match metrics with
        | Some sink -> sink.SetGauge(ComputeMemoMetrics.Entries, float entries.Count, Map.empty)
        | None -> ()

    // ── envelope (de)serialisation ────────────────────────────────────
    //
    // `Utf8JsonWriter` / `JsonDocument` rather than reflection over an F#
    // record, exactly as `BlobIdempotencyStore` does: the round-trip is
    // then independent of STJ's F#-record handling, and a corrupt or
    // foreign blob is a parse failure we can turn into a miss rather than
    // an exception out of a `Submit`.

    let serialise (key: ComputeMemoKey) (entry: ComputeMemoEntry) : byte[] =
        use ms = new IO.MemoryStream()

        (use writer = new Text.Json.Utf8JsonWriter(ms)
         writer.WriteStartObject()
         writer.WriteNumber("SchemaVersion", 1)
         writer.WriteString("ScopeId", key.ScopeId)
         writer.WriteString("Kind", key.Kind)
         writer.WriteString("PayloadHash", key.PayloadHash)
         writer.WriteString("IdempotencyKey", key.IdempotencyKey)
         writer.WriteString("Profile", key.Profile)
         writer.WriteString("ResultRef", entry.ResultRef)
         writer.WriteString("HandleId", entry.Handle.HandleId.ToString("N"))
         writer.WriteString("HandleBackend", entry.Handle.Backend)
         writer.WriteString("NativeRef", entry.Handle.NativeRef)
         writer.WriteNumber("SubmittedAtTicks", entry.Handle.SubmittedAt.Ticks)
         writer.WriteNumber("CachedAtTicks", entry.CachedAt.Ticks)
         writer.WriteNumber("ExpiresAtTicks", entry.ExpiresAt.Ticks)
         writer.WriteEndObject()
         writer.Flush())

        ms.ToArray()

    /// Parse an envelope, and refuse one that is not the entry for `key`.
    ///
    /// The key cross-check is the second half of the GP 4 guarantee: the
    /// path already partitions by scope, and this makes a mis-derived path
    /// or a digest collision degrade to a miss rather than to one scope
    /// reading another's result. `None` for a corrupt, foreign, or
    /// mismatched envelope — never an exception.
    let deserialise (key: ComputeMemoKey) (bytes: byte[]) : ComputeMemoEntry option =
        try
            use doc = Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            let root = doc.RootElement

            let readString (name: string) = root.GetProperty(name).GetString()

            let matches =
                readString "ScopeId" = key.ScopeId
                && readString "Kind" = key.Kind
                && readString "PayloadHash" = key.PayloadHash
                && readString "IdempotencyKey" = key.IdempotencyKey
                && readString "Profile" = key.Profile

            if not matches then
                None
            else
                Some {
                    Handle = {
                        HandleId = Guid.ParseExact(readString "HandleId", "N")
                        Backend = readString "HandleBackend"
                        ScopeId = key.ScopeId
                        NativeRef = readString "NativeRef"
                        SubmittedAt = DateTime(root.GetProperty("SubmittedAtTicks").GetInt64(), DateTimeKind.Utc)
                    }
                    ResultRef = readString "ResultRef"
                    CachedAt = DateTime(root.GetProperty("CachedAtTicks").GetInt64(), DateTimeKind.Utc)
                    ExpiresAt = DateTime(root.GetProperty("ExpiresAtTicks").GetInt64(), DateTimeKind.Utc)
                }
        with _ ->
            None

    let isLive (entry: ComputeMemoEntry) = now () < entry.ExpiresAt

    /// Drop an expired entry from both tiers. Best-effort on the blob —
    /// a delete that fails leaves an entry the next read will expire
    /// again, which is a wasted read and never a wrong answer.
    let discardExpired (key: ComputeMemoKey) (composite: string) = async {
        increment expired
        entries.Remove composite

        match blobs with
        | Some store ->
            let! _ = store.Delete(container, ComputeMemoLayout.entryBlob key.ScopeId (ComputeMemoKey.digest key))
            return ()
        | None -> return ()
    }

    /// The live entry for `key`, in-memory first and then the blob mirror.
    ///
    /// A blob hit primes the in-memory index, so the durable tier is read
    /// once per process per entry rather than once per submission — the
    /// restart-survival path costs one download, not a download per call.
    let tryLoad (key: ComputeMemoKey) (composite: string) : Async<ComputeMemoEntry option> = async {
        match entries.TryGet composite with
        | Some entry when isLive entry -> return Some entry
        | Some _ ->
            do! discardExpired key composite
            return None
        | None ->
            match blobs with
            | None -> return None
            | Some store ->
                let name = ComputeMemoLayout.entryBlob key.ScopeId (ComputeMemoKey.digest key)
                let! downloaded = store.Download(container, name)

                match downloaded with
                | Error _ -> return None
                | Ok bytes ->
                    match deserialise key bytes with
                    | None -> return None
                    | Some entry ->
                        if isLive entry then
                            entries.Set(composite, entry)
                            emitEntriesGauge ()
                            return Some entry
                        else
                            do! discardExpired key composite
                            return None
    }

    /// Record a terminal `Succeeded` outcome against `key`.
    ///
    /// A repeat store for the same composite is last-write-wins and
    /// benign: both writes describe the same work under the same key, and
    /// the later handle is the more recent of two equally valid ones.
    let store (key: ComputeMemoKey) (composite: string) (handle: ExternalHandle) (resultRef: string) = async {
        let cachedAt = now ()

        let entry = {
            Handle = handle
            ResultRef = resultRef
            CachedAt = cachedAt
            ExpiresAt = cachedAt + ttl
        }

        entries.Set(composite, entry)
        increment stored
        emitEntriesGauge ()

        match blobs with
        | Some blobStore ->
            // Best-effort: a memo that cannot persist still memoizes
            // in-process, so a blob failure must not turn a successful
            // poll into a failed one.
            let name = ComputeMemoLayout.entryBlob key.ScopeId (ComputeMemoKey.digest key)
            let! _ = blobStore.Upload(container, name, serialise key entry)
            return ()
        | None -> return ()
    }

    /// What the memo has done. The `Entries` / `MaxEntries` /
    /// `OverCapRecoveries` fields are the Phase 328 cap-pressure signal.
    member _.Stats: ComputeMemoStats = {
        Hits = Interlocked.Read(&hits[0])
        Misses = Interlocked.Read(&misses[0])
        Coalesced = Interlocked.Read(&coalesced[0])
        Stored = Interlocked.Read(&stored[0])
        Expired = Interlocked.Read(&expired[0])
        PollShortCircuits = Interlocked.Read(&pollShortCircuits[0])
        Evicted = entries.Evictions + byHandle.Evictions
        OverCapRecoveries = Interlocked.Read(&overCapRecoveries[0])
        Entries = entries.Count
        MaxEntries = cap
    }

    interface IExternalComputeDispatcher with
        /// The inner dispatcher's label, unchanged.
        ///
        /// Phase 478's choice, not Phase 484's: a single-dispatcher
        /// decorator has exactly one name it could truthfully use, and a
        /// handle must poll against a dispatcher that answers to the name
        /// it carries. The memo mints no handles of its own — a hit
        /// replays one the backend minted — so there is nothing to
        /// relabel.
        member _.Backend = inner.Backend

        member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
            match ComputeMemoKey.forSpec scopeId spec with
            | None ->
                // Not memoizable. One match, then Phase 318 exactly — no
                // key derivation, no hash, no lookup, no I/O (GP 13).
                return! inner.Submit(scopeId, spec)
            | Some key ->
                let composite = ComputeMemoKey.composite key

                match! tryLoad key composite with
                | Some entry ->
                    increment hits
                    emitCount ComputeMemoMetrics.HitsTotal key.Kind
                    // So a `Poll` of the replayed handle short-circuits
                    // too — a hit that then had to ask the backend for the
                    // outcome would only have moved the cost.
                    byHandle.Set(entry.Handle.HandleId, key)
                    // The inner dispatcher is not consulted at all: no
                    // dispatch, and no budget decorator below this one sees
                    // a submission to charge for.
                    return Ok entry.Handle
                | None ->
                    increment misses
                    emitCount ComputeMemoMetrics.MissesTotal key.Kind

                    // The coalescing window. A pre-built `Lazy` (not a
                    // factory delegate) is handed to `GetOrAdd`, so
                    // exactly one `Async.StartAsTask` can ever be forced
                    // for a key — `GetOrAdd`'s factory overload may invoke
                    // its delegate more than once under contention, which
                    // is precisely the double-dispatch being excluded.
                    let mine = lazy (Async.StartAsTask(inner.Submit(scopeId, spec)))

                    let held = inFlight.GetOrAdd(composite, mine)
                    let isLeader = LanguagePrimitives.PhysicalEquality held mine

                    if not isLeader then
                        increment coalesced
                        emitCount ComputeMemoMetrics.CoalescedTotal key.Kind

                    try
                        let! result = held.Value |> Async.AwaitTask

                        match result with
                        | Ok handle ->
                            // Written by every joiner, idempotently: a
                            // coalesced caller may return before the
                            // leader does, and the mapping is what lets
                            // its `Poll` store the outcome.
                            byHandle.Set(handle.HandleId, key)
                        | Error _ ->
                            // A refusal is not cached — see the header.
                            // Every joiner gets the same refusal, which is
                            // what a concurrent duplicate would have got
                            // from the backend anyway.
                            ()

                        return result
                    finally
                        // Only the leader retires the window, so a late
                        // joiner cannot remove an entry a newer submission
                        // has since created.
                        if isLeader then
                            inFlight.TryRemove composite |> ignore
        }

        member _.Poll(handle: ExternalHandle) = async {
            match byHandle.TryGet handle.HandleId with
            | None ->
                // A handle this process did not submit through the memo
                // (or one whose mapping has aged out). One dictionary
                // probe, then Phase 318 exactly — deliberately no blob
                // read, see the header.
                return! inner.Poll handle
            | Some key ->
                let composite = ComputeMemoKey.composite key

                match entries.TryGet composite with
                | Some entry when isLive entry ->
                    increment pollShortCircuits
                    return ExternalOutcome.Succeeded entry.ResultRef
                | _ ->
                    let! outcome = inner.Poll handle

                    match outcome with
                    | ExternalOutcome.Succeeded resultRef -> do! store key composite handle resultRef
                    | ExternalOutcome.Failed _
                    | ExternalOutcome.Cancelled ->
                        // Terminal but not cacheable. Drop the mapping so
                        // the handle stops carrying a claim on a cache
                        // entry that will never exist.
                        byHandle.Remove handle.HandleId
                    | ExternalOutcome.Pending
                    | ExternalOutcome.Running _ -> ()

                    return outcome
        }

        member _.Cancel(handle: ExternalHandle) =
            // Straight through, unconditionally. A cached outcome is
            // already terminal and Phase 318 requires cancelling a
            // terminal handle to be a non-throwing no-op, so there is
            // nothing for the memo to decide and no branch worth adding to
            // the path a non-memoizing deployment takes.
            inner.Cancel handle