module ToolUp.RAG.InMemoryVectorStore

open System
open System.Collections.Concurrent
open System.Threading
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IRagTelemetry

// ─── Vector math ──────────────────────────────────────────────────

let private dotProduct (a: float32 array) (b: float32 array) =
    if a.Length <> b.Length then
        // Dimension mismatch — almost always a stale chunk left behind by
        // an embedding model / provider swap. Truncating to the shorter
        // length (the old behaviour) produced a meaningless score that
        // still RANKED, silently corrupting retrieval. Returning 0 makes a
        // mismatched vector simply not match until ReembeddingService
        // re-embeds it: absent is recoverable, wrong-but-ranked is not.
        0.0f
    else
        let mutable sum = 0.0f

        for i in 0 .. a.Length - 1 do
            sum <- sum + a[i] * b[i]

        sum

let private magnitude (v: float32 array) =
    let mutable sum = 0.0f

    for x in v do
        sum <- sum + x * x

    sqrt sum

/// Return `v` scaled to unit length. Stored vectors are pre-normalised at
/// `Upsert` time so that cosine similarity at query time reduces to a single
/// dot product (`cos θ = dot(â, b̂)` when |â| = |b̂| = 1). A zero vector is
/// returned unchanged — its similarity to any query is always zero by
/// definition, and dividing by zero would produce NaN.
let private normalise (v: float32 array) =
    let m = magnitude v
    if m = 0.0f then v else Array.map (fun x -> x / m) v

// ─── Serialisable index entry ─────────────────────────────────────

[<CLIMutable>]
type IndexEntry = {
    ChunkId: string
    ScopeTag: string // JSON-serialised VectorScope
    Vector: float32 array
    Content: string
    Metadata: Map<string, string>
}

let private scopeToKey (scope: VectorScope) =
    match scope with
    | Platform -> "platform"
    | Deployment -> "deployment"
    | Team teamId -> $"team:{teamId}"
    | User userId -> $"user:{userId}"

let private blobName (scope: VectorScope) = $"_rag/{scopeToKey scope}/index.json"

// ─── JSON helpers ─────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private toJson o =
    JsonSerializer.Serialize(o, jsonOptions)

let private fromJson<'T> (s: string) =
    JsonSerializer.Deserialize<'T>(s, jsonOptions)

// ─── In-memory store ──────────────────────────────────────────────

/// In-memory vector store backed by IBlobStorage for persistence.
/// Each scope has its own index serialised to `_rag/{scopeKey}/index.json`.
///
/// Stored vectors are pre-normalised to unit length so similarity at query
/// time reduces to a single dot product per candidate. Persistence is
/// debounced: `Upsert`, `DeleteChunk`, and `DeleteByScope` mark the affected
/// scope dirty and return immediately; a background loop flushes the dirty
/// set at a bounded cadence. This amortises JSON serialisation cost during
/// bulk ingestion — a 10,000-chunk load triggers O(1) persistence passes
/// instead of O(10,000).
///
/// The store implements `IDisposable`. ASP.NET Core's DI container disposes
/// it during shutdown, which cancels the flush loop and performs one final
/// synchronous flush so no acknowledged writes are lost across restart.
///
/// Suitable for deployments with up to ~50,000 chunks; for larger corpora
/// replace with a distributed vector-database companion (Qdrant, pgvector, etc.)
/// — the `IVectorStore` interface satisfies all Phase 9c portability rules.
type InMemoryVectorStore
    (
        storage: IBlobStorage,
        ?logger: ILogger,
        ?flushIntervalMs: int,
        ?flushChunkThreshold: int,
        ?telemetry: IRagTelemetry,
        // Phase 14v — optional collaborators for corrupt-index observability.
        // `auditLog` records a `KnowledgeIndexLoadFailed` event; `notifications`
        // publishes a `SystemMessage` (Error) to platform admins on first
        // observed corruption per scope. Both optional — a deployment with
        // `NoAuditLog` / no channel still gets the telemetry counter + Warn
        // log line (GP 13: no collaborator, no cost).
        ?auditLog: IAuditLog,
        ?notifications: INotificationChannel
    ) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let flushMs = defaultArg flushIntervalMs 2000
    // Adaptive flush: when this many chunks have been marked dirty since the
    // last persist, fire an immediate flush instead of waiting for the
    // periodic timer. Keeps bulk-ingest durability within ~100 ms of the
    // batched embedding call without churning the persist path during
    // quiet periods. The time-based fallback still fires for trickle writes
    // that never cross the threshold.
    let flushThreshold = defaultArg flushChunkThreshold 100

    // (scopeKey, chunkId) → (normalisedVector, chunk)
    let store = ConcurrentDictionary<string * string, float32 array * TextChunk>()

    // Scopes that have been mutated since the last successful persist.
    // Keyed by scopeKey so duplicate marks collapse; the value preserves
    // the original `VectorScope` for `blobName` round-tripping.
    let dirty = ConcurrentDictionary<string, VectorScope>()

    let cts = new CancellationTokenSource()

    // Signals the flush loop that the chunk-count threshold has been crossed.
    // `SemaphoreSlim(0, 1)` collapses repeated signals during a burst — the
    // loop drains the dirty set in one pass regardless of how many writes
    // tripped the threshold while it was sleeping.
    let flushSignal = new SemaphoreSlim(0, 1)
    let mutable dirtyChunks = 0

    let makeKey scope chunkId = (scopeToKey scope, chunkId)

    let markDirty (scope: VectorScope) =
        dirty[scopeToKey scope] <- scope
        let n = Interlocked.Increment(&dirtyChunks)

        if n >= flushThreshold then
            try
                flushSignal.Release() |> ignore
            with :? SemaphoreFullException ->
                // Already signalled — flush loop will drain this batch on its
                // next pass; further increments don't need to re-signal.
                ()

    let persistScope (scope: VectorScope) = async {
        let scopeKey = scopeToKey scope

        let entries =
            store
            |> Seq.choose (fun kvp ->
                let (sk, cid) = kvp.Key

                if sk = scopeKey then
                    let (vec, chunk) = kvp.Value

                    Some {
                        ChunkId = cid
                        ScopeTag = sk
                        Vector = vec
                        Content = chunk.Content
                        Metadata = chunk.Metadata
                    }
                else
                    None)
            |> Seq.toList

        let bytes = toJson entries |> System.Text.Encoding.UTF8.GetBytes
        let! result = storage.Upload("_rag", blobName scope, bytes)

        match result with
        | Ok _ -> ()
        | Error e ->
            log.Warn $"[InMemoryVectorStore] Failed to persist scope {scopeKey}: {e} — will retry on next flush."
            // Re-mark dirty so the next flush retries this scope.
            markDirty scope
    }

    let flushAll () = async {
        // Reset the dirty-chunk counter at the start of the drain so that
        // writes arriving during persist re-arm the threshold for the next
        // pass. Combined with the snapshot-and-clear of `dirty` below, this
        // means a 1,000-chunk burst arriving during a flush produces at
        // most one extra signalled flush — not 1,000.
        let drainedDirtyChunks = Interlocked.Exchange(&dirtyChunks, 0)

        let scopes =
            // Snapshot-and-clear to avoid livelock: a concurrent Upsert that
            // arrives during this flush will re-mark the scope dirty and be
            // picked up on the next tick rather than blocking the current one.
            let captured = ResizeArray()

            for kvp in dirty do
                match dirty.TryRemove(kvp.Key) with
                | true, scope -> captured.Add scope
                | _ -> ()

            captured |> List.ofSeq

        if not scopes.IsEmpty then
            let sw = System.Diagnostics.Stopwatch.StartNew()

            for scope in scopes do
                try
                    do! persistScope scope
                with ex ->
                    log.Error($"[InMemoryVectorStore] Flush failed for {scopeToKey scope}", Some ex)
                    markDirty scope

            sw.Stop()

            match telemetry with
            | Some t -> t.RecordFlush(drainedDirtyChunks, sw.ElapsedMilliseconds)
            | None -> ()
    }

    // ─── Phase 14v — corrupt-index observability + refusal ───────────
    //
    // Blob container the index blobs live under. `blobName scope` is the
    // logical index path within it (`_rag/{scopeKey}/index.json`) — what an
    // operator greps for the corrupt artefact.
    let ragContainer = "_rag"

    // Scopes whose corrupt load has already been reported this process.
    // Keyed by scopeKey so a lazily-loaded team scope re-probed on every
    // `Search` (the corrupt load leaves it empty, so `ensureScopeLoaded`
    // re-attempts each time) emits telemetry + audit + a SystemMessage
    // exactly once, not once per query — the storm guard.
    let reportedCorruptions = ConcurrentDictionary<string, byte>()

    // Compliance-grade fail-loud toggle. When set, a corrupt-index load is
    // aborted with an actionable error instead of starting the scope empty;
    // for the eager Platform/Deployment loads this fails process startup.
    let refuseOnCorruption =
        match Environment.GetEnvironmentVariable "TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION" with
        | "1"
        | "true"
        | "TRUE" -> true
        | _ -> false

    /// Handle a scope-load deserialisation failure. Refusal (when enabled)
    /// is raised on every attempt, independent of the dedup — a corrupt
    /// blob must never load empty under fail-loud. The observability
    /// emissions (telemetry counter + `KnowledgeIndexLoadFailed` audit +
    /// deduplicated `SystemMessage`) fire once per scope per process.
    let handleCorruption (scope: VectorScope) (byteLen: int) (reason: string) = async {
        let scopeKey = scopeToKey scope
        let location = blobName scope

        if refuseOnCorruption then
            failwith
                $"[InMemoryVectorStore] Refusing to start scope '{scopeKey}' from a corrupt index blob '{location}' ({byteLen} bytes): {reason}. TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION is set — replace or delete the blob, then restart (unset the variable to fall back to the default silent-empty behaviour)."

        if reportedCorruptions.TryAdd(scopeKey, 0uy) then
            match telemetry with
            | Some t -> t.RecordIndexLoadError scopeKey
            | None -> ()

            match auditLog with
            | Some a ->
                do!
                    a.Record(
                        KnowledgeSourceModule.value,
                        KnowledgeIndexLoadFailed {
                            ScopeKey = scopeKey
                            Reason = reason
                            Bytes = byteLen
                            BlobLocation = location
                        }
                    )
            | None -> ()

            match notifications with
            | Some ch ->
                let text =
                    $"RAG knowledge index for scope '{scopeKey}' failed to load (corrupt blob '{location}', {byteLen} bytes): {reason}. Retrieval for this scope is running empty until the blob is repaired."

                do!
                    ch.Publish(
                        NotificationKind.PlatformReservedScope,
                        Notification.SystemMessage(SystemMessageLevel.Error, text)
                    )
            | None -> ()

            log.Warn
                $"[InMemoryVectorStore] Corrupt index for {scopeKey}: {reason} — starting empty (telemetry + audit + SystemMessage emitted)."
        else
            log.Warn
                $"[InMemoryVectorStore] Corrupt index for {scopeKey}: {reason} — starting empty (already reported this process)."
    }

    let loadScope (scope: VectorScope) = async {
        match! storage.Download(ragContainer, blobName scope) with
        | Ok bytes ->
            // Parse in a pure try/with so the corruption handler (which
            // awaits audit + notification) runs outside the exception frame.
            let entriesResult =
                try
                    let json = System.Text.Encoding.UTF8.GetString bytes
                    Ok(fromJson<IndexEntry list> json)
                with ex ->
                    Error ex.Message

            match entriesResult with
            | Ok entries ->
                for e in entries do
                    let chunk = {
                        Content = e.Content
                        Metadata = e.Metadata
                    }
                    // Re-normalise on load: historical indexes may predate
                    // upsert-time normalisation; normalising an already-unit
                    // vector is a no-op modulo float rounding.
                    let unit = normalise e.Vector

                    store.AddOrUpdate((e.ScopeTag, e.ChunkId), (unit, chunk), fun _ _ -> (unit, chunk))
                    |> ignore
            | Error reason -> do! handleCorruption scope bytes.Length reason
        | Error _ -> ()
    }

    // Load Platform and Deployment scopes eagerly at construction.
    // Team and User scopes are loaded lazily on first access (team/user
    // IDs are not known at construction time).
    do
        loadScope Platform |> Async.RunSynchronously
        loadScope Deployment |> Async.RunSynchronously

    // Background flush loop: waits for either the periodic `flushMs` timer or
    // a `flushSignal.Release()` from `markDirty` once the dirty-chunk count
    // crosses `flushThreshold`. `WaitAsync` returns `true` on signal and
    // `false` on timeout — both paths drain the dirty set the same way.
    // Exceptions are logged and affected scopes are re-marked dirty for
    // retry on the next pass.
    let flushLoop = async {
        while not cts.IsCancellationRequested do
            try
                let! _ = flushSignal.WaitAsync(flushMs, cts.Token) |> Async.AwaitTask

                if not cts.IsCancellationRequested then
                    do! flushAll ()
            with
            | :? OperationCanceledException -> ()
            | ex -> log.Error("[InMemoryVectorStore] Flush loop error", Some ex)
    }

    do Async.Start(flushLoop, cts.Token)

    let isTombstoned (chunk: TextChunk) =
        chunk.Metadata.ContainsKey ChunkMetadata.DeletedAtKey

    let scopeFromKey (sk: string) =
        if sk = "platform" then
            Platform
        elif sk = "deployment" then
            Deployment
        elif sk.StartsWith "user:" then
            User(sk.Substring("user:".Length))
        else
            Team(sk.Substring("team:".Length))

    let ensureScopeLoaded (scope: VectorScope) = async {
        // Team AND User scopes are lazy-loaded on first access — neither id
        // is known at construction time, so only Platform/Deployment are
        // loaded eagerly (see the eager `do` block above).
        match scope with
        | Team _
        | User _ ->
            let scopeKey = scopeToKey scope
            let hasAny = store.Keys |> Seq.exists (fun (sk, _) -> sk = scopeKey)

            if not hasAny then
                do! loadScope scope
        | _ -> ()
    }

    interface IVectorStore with

        member _.Upsert scope chunkId vector chunk = async {
            let unit = normalise vector
            // Re-upserting clears any pre-existing tombstone — new content
            // supersedes the old whether or not it's been vacuumed.
            let cleanedMetadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey

            let cleanedChunk = {
                chunk with
                    Metadata = cleanedMetadata
            }

            store.AddOrUpdate(makeKey scope chunkId, (unit, cleanedChunk), fun _ _ -> (unit, cleanedChunk))
            |> ignore

            markDirty scope
        }

        member _.Search scopes query topK = async {
            // Ensure requested team scopes are loaded — lazy hydration from blob.
            // Platform/Deployment are already loaded at construction.
            for scope in scopes do
                do! ensureScopeLoaded scope

            let scopeKeys = scopes |> List.map scopeToKey |> Set.ofList

            // Normalise the query once; every stored vector is already unit
            // length, so the dot product is exactly the cosine similarity.
            let queryUnit = normalise query

            let results =
                store
                |> Seq.choose (fun kvp ->
                    let (sk, cid) = kvp.Key

                    if scopeKeys.Contains sk then
                        let (vec, chunk) = kvp.Value
                        // Tombstoned chunks are invisible to search.
                        if isTombstoned chunk then
                            None
                        else
                            let score = float (dotProduct queryUnit vec)
                            Some(sk, cid, score, chunk)
                    else
                        None)
                // Total-order sort: `store` is a ConcurrentDictionary
                // enumerated in unspecified order, and equal cosine scores
                // are common (TF-IDF vectors over a small corpus collide).
                // A score-only sort would resolve ties by enumeration order
                // and flip top-K membership run-to-run. Tie-break on the
                // unique `(scopeKey, chunkId)`.
                |> Seq.sortBy (fun (sk, cid, score, _) -> -score, sk, cid)
                |> Seq.truncate topK
                |> Seq.map (fun (sk, cid, score, chunk) -> {
                    ChunkId = cid
                    Content = chunk.Content
                    Score = score
                    Scope = scopeFromKey sk
                    Metadata = chunk.Metadata
                })
                |> Seq.toList

            return results
        }

        member _.ListChunks scope includeDeleted = async {
            do! ensureScopeLoaded scope
            let scopeKey = scopeToKey scope

            let results =
                store
                |> Seq.choose (fun kvp ->
                    let (sk, cid) = kvp.Key

                    if sk = scopeKey then
                        let (_, chunk) = kvp.Value

                        if includeDeleted || not (isTombstoned chunk) then
                            Some(cid, chunk)
                        else
                            None
                    else
                        None)
                |> Seq.toList

            return results
        }

        member _.DeleteByScope scope = async {
            let scopeKey = scopeToKey scope
            let toRemove = store.Keys |> Seq.filter (fun (sk, _) -> sk = scopeKey) |> Seq.toList

            for key in toRemove do
                store.TryRemove(key) |> ignore

            markDirty scope
        }

        member _.DeleteChunk scope chunkId = async {
            // Tombstone-write: stamp `_deletedAt` and keep the entry. Vacuum
            // is the path that hard-removes tombstoned entries past their
            // retention window.
            let key = makeKey scope chunkId

            match store.TryGetValue key with
            | true, (vec, chunk) ->
                let stamped = {
                    chunk with
                        Metadata =
                            chunk.Metadata
                            |> Map.add ChunkMetadata.DeletedAtKey (DateTimeOffset.UtcNow.ToString("o"))
                }

                store.AddOrUpdate(key, (vec, stamped), fun _ _ -> (vec, stamped)) |> ignore

                markDirty scope
            | _ -> ()
        }

        member _.RestoreChunk scope chunkId = async {
            let key = makeKey scope chunkId

            match store.TryGetValue key with
            | true, (vec, chunk) when isTombstoned chunk ->
                let restored = {
                    chunk with
                        Metadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey
                }

                store.AddOrUpdate(key, (vec, restored), fun _ _ -> (vec, restored)) |> ignore

                markDirty scope
            | _ -> ()
        }

        member _.Vacuum scope olderThan = async {
            let scopeKey = scopeToKey scope

            let toPurge =
                store
                |> Seq.choose (fun kvp ->
                    let (sk, _) = kvp.Key

                    if sk = scopeKey then
                        let (_, chunk) = kvp.Value

                        match chunk.Metadata.TryFind ChunkMetadata.DeletedAtKey with
                        | Some ts ->
                            match DateTimeOffset.TryParse ts with
                            | true, parsed when parsed < olderThan -> Some kvp.Key
                            | _ -> None
                        | None -> None
                    else
                        None)
                |> Seq.toList

            for key in toPurge do
                store.TryRemove key |> ignore

            if not toPurge.IsEmpty then
                markDirty scope

            return toPurge.Length
        }

        member _.ListScopes() = async {
            let keys = store.Keys |> Seq.map fst |> Seq.distinct |> Seq.toList
            return keys |> List.map scopeFromKey
        }

        member this.Erase(scope, subjectUserId, policy, dryRun) =
            ToolUp.Platform.IVectorStore.eraseSubject (this :> IVectorStore) scope subjectUserId policy dryRun

    interface IDisposable with
        member _.Dispose() =
            // Stop the background loop, then perform one final synchronous
            // flush so no acknowledged writes are lost across process shutdown.
            cts.Cancel()

            try
                flushAll () |> Async.RunSynchronously
            with ex ->
                log.Error("[InMemoryVectorStore] Final flush on dispose failed", Some ex)

            cts.Dispose()
            flushSignal.Dispose()