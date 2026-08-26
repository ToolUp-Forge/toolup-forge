namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69f — per-method idempotency keys
// =============================================================================
//
// API record fields can carry `[<Idempotent>]` to opt into client-key-driven
// memoisation. The dispatcher requires an `X-Idempotency-Key` header on
// matching calls; the first call's response is captured and replayed on
// subsequent calls with the same key (within TTL).
//
// Key shape: `{subjectId}|{methodName}|{idempotencyKey}` so the same UUID
// submitted by two different subjects is two distinct cache slots — a
// security boundary, not a sharing one.
//
// Order in the dispatcher pre-flight chain:
//   auth (69d) → idempotency (69f) → rate-limit (69g) → handler
// Rationale: an idempotency cache hit means we're replaying a response
// that was already counted against rate-limit on its first call;
// re-counting it would double-charge the budget.

/// Phase 69f — opts a method into client-key-driven idempotency. Calls
/// against an `[<Idempotent>]` method MUST carry an `X-Idempotency-Key`
/// header; the dispatcher denies with `RemotingError.User` envelope
/// when the header is missing AND a store is composed. When no store is
/// composed, the attribute is dormant (the method runs normally on
/// every call).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type IdempotentAttribute() =
    inherit Attribute()

// -----------------------------------------------------------------------------

/// Phase 69f — default in-memory implementation of `IIdempotencyStore`.
/// Concurrent dictionary keyed on `{scope}|{key}`; TTL eviction is lazy
/// (checked on read). Bounded by `maxEntries` (default 100_000) — once
/// the cap is reached, the oldest entry (by insertion order) is evicted
/// on next `Store`. Cardinality-bounded so a burst of unique keys
/// (e.g. retry storms with fresh GUIDs) can't OOM the host.
///
/// Phase 328 — eviction is a **bounded FIFO drain**, never a mass wipe. If
/// the FIFO queue is momentarily empty while the dictionary is still over
/// cap (a count/queue race under concurrent inserts), the store accepts a
/// transient slight over-cap rather than discarding live keys, and records
/// the event (`OverCapRecoveryCount` + an optional `logger` `Warn`) so the
/// over-cap path is observable. The optional `logger` is used only for that
/// signal; omit it to stay silent (behaviour-preserving default, GP 11).
type InMemoryIdempotencyStore(?maxEntries: int, ?logger: ToolUp.Platform.ILogger) =
    let cap = defaultArg maxEntries 100_000

    // Phase 328 — bound the per-`Store` eviction work. A single `Store` adds
    // exactly one entry, so steady-state eviction removes one victim; this
    // caps the catch-up drain after a concurrent-insert burst so no one call
    // spins unboundedly. Leaving a residue is fine — the next `Store`
    // continues the drain (transient slight over-cap).
    let drainBatch = 64

    let entries =
        ConcurrentDictionary<string, struct (DateTimeOffset * MemoisedResponse)>()
    // Insertion-order queue for FIFO eviction when the dictionary
    // exceeds `cap`. ConcurrentQueue is approximately ordered; exact
    // ordering isn't required for the LRU semantics.
    let order = ConcurrentQueue<string>()

    // Phase 328 (concurrency fix) — the eviction bookkeeping spans two
    // structures (`entries` + `order`) that MUST stay in lockstep: every live
    // entry has to retain exactly one FIFO marker, or it becomes unevictable.
    // The read-modify-write across both is not atomic on the lock-free
    // primitives — most acutely, `compactStaleHeads` peeks the head, decides
    // it is stale, then dequeues; if a concurrent evictor removes that head in
    // the window between the peek and the dequeue, the dequeue discards the
    // *new* head instead, throwing away a **live** key's marker. That orphans
    // the entry (present in `entries`, absent from `order`) — unevictable, so
    // it squats a cap slot forever and forces later inserts to evict the
    // *newest* keys instead of the oldest. This `gate` serialises the write
    // path so `order` has a single writer; `TryGet` stays lock-free (its
    // lazy-expiry removal only ever leaves a stale marker, which
    // `compactStaleHeads` reclaims safely once it is the sole `order` mutator).
    let gate = obj ()

    // Phase 328 — cumulative count of over-cap recovery events (the branch
    // that formerly wiped the entire cache with `entries.Clear()`). A
    // heap-backed single-element int64 so the lock-free `Interlocked`
    // increment can take a byref to it from inside the `evictOldestIfFull`
    // closure — a `let mutable` field cannot be address-taken through a
    // closure, an array element can. Surfaced via `OverCapRecoveryCount`.
    let overCapRecoveries: int64[] = Array.zeroCreate 1

    let compositeKey scope key = scope + "|" + key

    /// Opportunistically drain stale heads from `order` — keys that
    /// were already removed from `entries` (e.g. lazy TTL expiry, or
    /// re-added keys leaving a stale FIFO marker). Keeps `order.Count`
    /// close to `entries.Count` so subsequent eviction work doesn't
    /// scan through ghost entries.
    ///
    /// `TryPeek` and `TryDequeue` are each atomic but NOT atomic *together*:
    /// a naive `peek head; if stale then dequeue head` races a concurrent
    /// inserter/evictor that advances the head between the two calls, so the
    /// `TryDequeue` would discard a *live* key's marker (the new head) that was
    /// never inspected. That live entry then has no FIFO marker at all — an
    /// orphan — so `order` drifts below `entries`, and once the queue runs out
    /// of markers for the genuinely-old keys, eviction starts dropping the
    /// *newest* keys instead of the oldest (a freshly-stored idempotency key
    /// evicted ⇒ replay miss ⇒ handler re-execution — the very failure this
    /// store guards against). So decide staleness against the value we
    /// *actually* dequeued, and if it turns out to be live (the head raced
    /// forward), re-enqueue it rather than orphan it. Re-queuing to the tail
    /// perturbs FIFO order slightly, which the approximate-ordering contract on
    /// `order` already permits.
    let compactStaleHeads () =
        let mutable keepCompacting = true

        while keepCompacting do
            let mutable peeked = Unchecked.defaultof<string>

            if order.TryPeek(&peeked) && not (entries.ContainsKey peeked) then
                let mutable dequeued = Unchecked.defaultof<string>

                if order.TryDequeue(&dequeued) then
                    if entries.ContainsKey dequeued then
                        // The head advanced between peek and dequeue: we pulled
                        // a live marker. Put it back — never orphan a live entry.
                        order.Enqueue dequeued
                        keepCompacting <- false
                // else: genuinely stale head dropped; keep compacting.
                else
                    keepCompacting <- false
            else
                keepCompacting <- false

    /// Phase 328 — the over-cap recovery signal. Increments the cumulative
    /// counter and emits an operator-visible `Warn`. This fires only on the
    /// genuine cap-race (a non-empty FIFO queue never reaches here), so it
    /// is self-throttling — no log flood under normal at-cap operation. It
    /// replaces the silent `entries.Clear()` mass-wipe.
    let recordOverCapRecovery () =
        let total = System.Threading.Interlocked.Increment(&overCapRecoveries[0])

        match logger with
        | Some log ->
            log.Warn(
                sprintf
                    "InMemoryIdempotencyStore: FIFO eviction queue empty while at/over the %d-entry cap — accepting a transient over-cap instead of clearing the cache (cumulative over-cap recoveries: %d). Sustained occurrences mean the idempotency store is under-provisioned for this instance's traffic; raise maxEntries or wire a distributed IIdempotencyStore."
                    cap
                    total
            )
        | None -> ()

    // Phase 328 — bounded FIFO drain. Formerly the `else` branch below
    // called `entries.Clear()`: under a count/queue race at the cap (a
    // concurrent inserter has added to `entries` but not yet enqueued its
    // key, so `order` is momentarily empty while `entries.Count >= cap`)
    // that wiped EVERY memoised response — so every in-flight idempotency
    // key then missed and re-executed its handler (double-charge / duplicate
    // side effect), silently, and only under cap pressure. The drain now
    // evicts at most `drainBatch` oldest entries and, when the queue is
    // empty, accepts a transient slight over-cap rather than discarding live
    // keys, recording the recovery so it is observable.
    let evictOldestIfFull () =
        compactStaleHeads ()

        let mutable drained = 0
        let mutable keepDraining = true

        while keepDraining && entries.Count >= cap do
            let mutable victim = Unchecked.defaultof<string>

            if order.TryDequeue(&victim) then
                entries.TryRemove victim |> ignore
                drained <- drained + 1
                // Bounded: cap a single `Store`'s eviction work. If still
                // over cap after a full batch (a large concurrent-insert
                // burst), accept the transient over-cap — the next `Store`
                // continues the drain.
                if drained >= drainBatch then
                    keepDraining <- false
            else
                // Cap-race: dictionary at/over cap but the FIFO queue is
                // momentarily empty. Accept a transient slight over-cap and
                // record the recovery; NEVER wipe the cache (that would
                // re-execute every in-flight idempotency key). The next
                // `Store` drains the residue once the concurrent enqueue
                // lands.
                keepDraining <- false
                recordOverCapRecovery ()

    interface IIdempotencyStore with
        member _.TryGet(key, scope) = async {
            let now = DateTimeOffset.UtcNow
            let composite = compositeKey scope key

            match entries.TryGetValue composite with
            | true, struct (expiry, response) ->
                if now < expiry then
                    return Some response
                else
                    // Lazy eviction.
                    entries.TryRemove composite |> ignore
                    return None
            | false, _ -> return None
        }

        member _.Store(key, scope, response, ttl) = async {
            let expiry = DateTimeOffset.UtcNow + ttl
            let composite = compositeKey scope key

            // Serialise eviction + insert so `entries` and `order` never
            // desync (see the `gate` note above). The critical section is a
            // handful of dictionary/queue ops — no I/O, no awaits — so holding
            // the lock is cheap for an in-process store; distributed
            // deployments wire a Redis/DynamoDB impl with its own atomicity.
            lock gate (fun () ->
                evictOldestIfFull ()
                entries[composite] <- struct (expiry, response)
                order.Enqueue composite)
        }

    /// Diagnostics: current entry count, for telemetry / health checks.
    member _.Count = entries.Count
    /// Diagnostics: configured cap.
    member _.MaxEntries = cap

    /// Phase 328 — diagnostics: cumulative number of over-cap recovery
    /// events (the FIFO queue was momentarily empty at/over the cap, so a
    /// transient over-cap was accepted rather than discarding live keys). A
    /// non-zero and climbing value is the observable signal that this
    /// instance's idempotency store is under cap pressure. This path
    /// formerly wiped the entire cache silently.
    member _.OverCapRecoveryCount =
        System.Threading.Interlocked.Read(&overCapRecoveries[0])

// -----------------------------------------------------------------------------

/// Phase 164 — shared layout for the blob-backed idempotency store, so the
/// TTL sweep job (`IdempotencySweepJob`, compiled after the job substrate)
/// enumerates + parses the same blobs `BlobIdempotencyStore` writes without
/// duplicating the container / prefix / envelope-shape knowledge.
module BlobIdempotencyLayout =
    /// Reserved SDK-level container the idempotency envelopes live under.
    [<Literal>]
    let DefaultContainer = "_platform"

    /// Blob-name prefix shared by every idempotency envelope (the `List`
    /// argument the sweep enumerates).
    [<Literal>]
    let BlobPrefix = "idem/"

    /// Read the absolute expiry (UTC ticks) from a stored envelope; `None`
    /// when the blob is corrupt / not an idempotency envelope — the sweep
    /// then leaves it untouched rather than crashing or mis-deleting.
    let tryReadExpiryTicks (bytes: byte[]) : int64 option =
        try
            use doc = System.Text.Json.JsonDocument.Parse(System.ReadOnlyMemory<byte>(bytes))
            Some(doc.RootElement.GetProperty("ExpiryUtcTicks").GetInt64())
        with _ ->
            None

// -----------------------------------------------------------------------------

/// Phase 69f.C — distributed `IIdempotencyStore` over `IBlobStorage`.
/// Memoised entries are JSON blobs under the `_platform` container (the
/// reserved SDK-level scope), named by a SHA-256 of `{scope}|{key}` so
/// arbitrary subject ids / method names / client keys produce a safe,
/// collision-resistant blob name. TTL is carried in the envelope and
/// enforced lazily on `TryGet` (an expired entry is read as a miss and
/// best-effort deleted).
///
/// **Concurrency.** `IBlobStorage` exposes no conditional-write / ETag
/// surface, so two requests racing the same key both miss-then-store and
/// the second overwrites the first — last-write-wins. The handler is
/// idempotent by contract, so both writes carry the same response and
/// the race is benign for replay correctness; it does NOT guarantee
/// exactly-once *handler invocation* under a concurrent race (neither
/// does the in-process default — that needs a conditional-write the
/// interface doesn't model). Distributed deployments that need stricter
/// once-only semantics wire a store with a compare-and-set primitive
/// (Redis SETNX / DynamoDB conditional put) against the same contract.
type BlobIdempotencyStore(blobStorage: ToolUp.Platform.BlobStorage.IBlobStorage, ?container: string) =
    let container = defaultArg container BlobIdempotencyLayout.DefaultContainer

    let blobName (scope: string) (key: string) : string =
        use sha = System.Security.Cryptography.SHA256.Create()
        let bytes = System.Text.Encoding.UTF8.GetBytes(scope + "|" + key)
        let hash = System.Convert.ToHexString(sha.ComputeHash bytes).ToLowerInvariant()
        BlobIdempotencyLayout.BlobPrefix + hash + ".json"

    // Serialise the envelope with `Utf8JsonWriter` / read it with
    // `JsonDocument` — no reflection over an F# record type, so the
    // round-trip is robust regardless of STJ's F#-record handling. The
    // body is base64 so the JSON stays text-clean for binary responses.
    let serialise (response: MemoisedResponse) (expiryTicks: int64) : byte[] =
        use ms = new System.IO.MemoryStream()

        (use writer = new System.Text.Json.Utf8JsonWriter(ms)
         writer.WriteStartObject()
         writer.WriteNumber("StatusCode", response.StatusCode)
         writer.WriteString("ContentType", response.ContentType)
         writer.WriteString("RequestBodyHash", response.RequestBodyHash)
         writer.WriteNumber("ExpiryUtcTicks", expiryTicks)
         writer.WriteString("BodyBase64", System.Convert.ToBase64String response.Body)
         writer.WriteEndObject()
         writer.Flush())

        ms.ToArray()

    interface IIdempotencyStore with
        member _.TryGet(key, scope) = async {
            let name = blobName scope key
            let! downloaded = blobStorage.Download(container, name)

            match downloaded with
            | Ok bytes ->
                try
                    use doc = System.Text.Json.JsonDocument.Parse(System.ReadOnlyMemory<byte>(bytes))
                    let root = doc.RootElement
                    let expiry = root.GetProperty("ExpiryUtcTicks").GetInt64()

                    if DateTimeOffset.UtcNow.UtcTicks < expiry then
                        return
                            Some {
                                Body = System.Convert.FromBase64String(root.GetProperty("BodyBase64").GetString())
                                StatusCode = root.GetProperty("StatusCode").GetInt32()
                                ContentType = root.GetProperty("ContentType").GetString()
                                RequestBodyHash = root.GetProperty("RequestBodyHash").GetString()
                            }
                    else
                        // Lazy TTL expiry — best-effort delete, read as miss.
                        let! _ = blobStorage.Delete(container, name)
                        return None
                with _ ->
                    // Corrupt / unreadable envelope is a miss, never a crash.
                    return None
            | Error _ -> return None
        }

        member _.Store(key, scope, response, ttl) = async {
            let expiryTicks = (DateTimeOffset.UtcNow + ttl).UtcTicks
            let! _ = blobStorage.Upload(container, blobName scope key, serialise response expiryTicks)
            return ()
        }

// -----------------------------------------------------------------------------

module internal Idempotency =

    // Reflect over public AND non-public records, and recognise BOTH the
    // server-tier `IdempotentAttribute` and the tier-shared
    // `ToolUp.Platform.IdempotentAttribute` mirror (which Fable-compiled
    // Core API records carry) — same family-agnostic + fail-open fix as
    // the 69d/69h/69g/69e classifiers. Without it a Core API record's
    // `[<Idempotent>]` is invisible (idempotency never engages), and a
    // non-public record silently skips classification.
    let private reflectionFlags =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    // ── Phase 727 severity assessment — the idempotency family ────────
    //
    // What a forgery buys:
    //
    //   * HONOURED, a foreign `IdempotentAttribute` arms response
    //     memoisation on a method the consumer never marked: a second
    //     call carrying the same `X-Idempotency-Key` is answered from the
    //     store instead of executing the handler. That is a CORRECTNESS
    //     defect (a stale answer where a fresh execution was intended),
    //     and it is bounded in the direction that matters most: the store
    //     scope is `subjectKey|methodName` (`deriveScope`), so a memoised
    //     response can only ever be replayed to the SAME subject — a
    //     forged marker cannot leak one caller's response to another. It
    //     is also doubly opt-in: dormant unless an `IIdempotencyStore` is
    //     composed (GP 13) AND the caller sends the header.
    //   * NOT honoured — the direction a bare identity fix introduces —
    //     is the sharper one: a consumer whose own `IdempotentAttribute`
    //     was picked up by accident loses replay protection silently, and
    //     the calls that carry an idempotency key are the ones where
    //     duplicate execution is expensive (payments, provisioning).
    //
    // Same shape as rate limiting, one notch lower in blast radius
    // because of the per-subject scope and the double opt-in. VERDICT:
    // fix — CLR identity + collision refusal. Severity MEDIUM
    // (correctness; no cross-subject exposure).
    let private markers =
        MarkerFamily [ typeof<IdempotentAttribute>; typeof<ToolUp.Platform.IdempotentAttribute> ]

    let private isIdempotentAttr (a: obj) : bool = markers.IsSanctioned(a.GetType())

    /// Phase 727 — marker-name collisions on the API record, for the
    /// dispatcher's startup refusal.
    let foreignMarkers (apiType: Type) : (string * string * string) list =
        markers.Collisions(apiType, reflectionFlags)
        |> List.map (fun (field, rendered) -> "idempotency", field, rendered)

    /// Cache the `[<Idempotent>]` classification per method at startup.
    let classify (apiType: Type) : Set<string> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Set.empty
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun pi ->
                let hasIdempotent = pi.GetCustomAttributes(true) |> Array.exists isIdempotentAttr

                if hasIdempotent then Some pi.Name else None)
            |> Set.ofArray

    /// Derive the scope string for the idempotency store from the
    /// subject id + method name. Same shape as the rate-limit key root
    /// so the two seams partition the same way.
    let deriveScope (subjectKey: string) (methodName: string) : string = subjectKey + "|" + methodName

    /// 0.1.15 — SHA-256 (lowercase hex) of the request body. Stamped
    /// into the `MemoisedResponse` on first call; compared against the
    /// current call's body on replay. Mismatch surfaces as a
    /// `ErrorCategory.User` envelope + 409 Conflict so the caller
    /// notices the idempotency-key reuse instead of receiving the
    /// prior response.
    ///
    /// 0.1.16 — accepts the body as `byte[]` rather than `string`.
    /// The 0.1.15 string shape went through a UTF-8 decode that
    /// silently replaced invalid bytes with U+FFFD, producing
    /// false-match hashes for any multipart upload containing
    /// pathological byte sequences. The raw-bytes form is the
    /// only correct hash for binary bodies (multipart uploads).
    /// The `hashRequestBodyText` wrapper is kept for JSON callers
    /// who already have the text in hand.
    let hashRequestBodyBytes (bodyBytes: byte[]) : string =
        use sha = System.Security.Cryptography.SHA256.Create()
        let hash = sha.ComputeHash bodyBytes
        System.Convert.ToHexString(hash).ToLowerInvariant()

    let hashRequestBody (bodyText: string) : string =
        let bytes = System.Text.Encoding.UTF8.GetBytes(bodyText)
        hashRequestBodyBytes bytes