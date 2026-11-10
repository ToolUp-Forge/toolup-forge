module ToolUp.RAG.InMemoryVectorStore

open System
open System.Collections.Generic
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.IRagTelemetry
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

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


// ─── In-memory store ──────────────────────────────────────────────

/// One scope's live form: chunk id → (unit-length vector, chunk).
type private ScopeEntries = Dictionary<string, float32 array * TextChunk>

let private layout = SnapshotLayout.rag "index.json"

let private isTombstoned (chunk: TextChunk) =
    chunk.Metadata.ContainsKey ChunkMetadata.DeletedAtKey

/// The live form of a scope's persisted rows. Vectors are re-normalised on
/// load: historical indexes may predate upsert-time normalisation, and
/// normalising an already-unit vector is a no-op modulo float rounding.
let private materialise (_: VectorScope) (rows: IndexEntry list) : ScopeEntries * bool =
    let live = ScopeEntries()

    for row in rows do
        live[row.ChunkId] <-
            (normalise row.Vector,
             {
                 Content = row.Content
                 Metadata = row.Metadata
             })

    live, false

/// The persisted rows of one scope's live form.
let private rowsOf (scope: VectorScope) (live: ScopeEntries) : IndexEntry list =
    let scopeKey = VectorScope.key scope

    [
        for KeyValue(chunkId, (vector, chunk)) in live do
            {
                ChunkId = chunkId
                ScopeTag = scopeKey
                Vector = vector
                Content = chunk.Content
                Metadata = chunk.Metadata
            }
    ]

/// In-memory vector store backed by `IBlobStorage` for persistence, over the
/// shared `ScopedSnapshotIndex`. Each scope's index is serialised to
/// `_rag/{scopeKey}/index.json`, and a search reads only the requested
/// scopes' own maps.
///
/// Stored vectors are pre-normalised to unit length so similarity at query
/// time reduces to a single dot product per candidate. Persistence is
/// debounced: a mutation marks its scope dirty and returns; the index flushes
/// the dirty set on a timer (`flushIntervalMs`, two seconds by default), or as
/// soon as `flushChunkThreshold` mutations (100 by default) have been marked,
/// so a 10,000-chunk load costs a handful of persistence passes, not 10,000.
///
/// A corrupt snapshot follows `corruptPolicy` (read from
/// `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` when not supplied) and is reported
/// through `telemetry`, `auditLog` and `notifications`, once per scope; each
/// is optional, and the store logs either way.
///
/// The store implements `IDisposable`: disposing it stops the flush loop and
/// flushes once more, so no acknowledged write is lost across restart.
/// `composeRAG` registers the store it constructs as an instance, which the
/// DI container never disposes, so its `RetrievalStoreFlushService` starts
/// it and disposes it. A store you construct and supply yourself is yours to
/// dispose; `Start` is optional for it, since every scope loads on first access.
///
/// Suitable for deployments with up to ~50,000 chunks; for larger corpora
/// replace with a distributed vector-database companion (Qdrant, pgvector, …).
type InMemoryVectorStore
    (
        storage: IBlobStorage,
        ?logger: ILogger,
        ?flushIntervalMs: int,
        ?flushChunkThreshold: int,
        ?telemetry: IRagTelemetry,
        ?auditLog: IAuditLog,
        ?notifications: INotificationChannel,
        ?corruptPolicy: CorruptPolicy
    ) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let policy: SnapshotPolicy = {
        FlushIntervalMs = defaultArg flushIntervalMs 2000
        FlushChunkThreshold = Some(defaultArg flushChunkThreshold 100)
        FlushTelemetry = telemetry
        OnCorrupt = {
            Policy = corruptPolicy |> Option.defaultWith CorruptPolicy.fromEnvironment
            Telemetry = telemetry
            AuditLog = defaultArg auditLog (NoOpAuditLog() :> IAuditLog)
            Notifications = notifications
        }
    }

    let index =
        new ScopedSnapshotIndex<IndexEntry list, ScopeEntries>(
            storage,
            layout,
            BlobCodec.json<IndexEntry list>,
            (fun _ -> ScopeEntries()),
            materialise,
            (fun scope live -> Some(rowsOf scope live)),
            policy,
            "InMemoryVectorStore",
            log
        )

    /// Load the platform and deployment scopes now (see
    /// `ScopedSnapshotIndex.Start`).
    member _.Start() = index.Start()

    /// The index is this process's in-memory map; each replica
    /// holds its own (flushed to blob storage, but searched from memory).
    interface IVectorStoreLocality with
        member _.IndexLocality = Some VectorIndexLocality.InProcess

    interface IVectorStore with

        member _.Upsert scope chunkId vector chunk =
            // Re-upserting clears any pre-existing tombstone — new content
            // supersedes the old whether or not it's been vacuumed.
            let cleaned = {
                chunk with
                    Metadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey
            }

            let unit = normalise vector

            index.Mutate(
                scope,
                fun live ->
                    live[chunkId] <- (unit, cleaned)
                    (), true
            )

        member _.Search scopes query topK = async {
            for scope in scopes do
                do! index.EnsureLoadedForSearch scope

            // Normalise the query once; every stored vector is already unit
            // length, so the dot product is exactly the cosine similarity.
            let queryUnit = normalise query

            // Each scope is scored from its own map; no other scope's entries
            // are read.
            let scored = [
                for scope in scopes do
                    let hits =
                        index.Read(
                            scope,
                            fun live -> [
                                for KeyValue(chunkId, (vector, chunk)) in live do
                                    if not (isTombstoned chunk) then
                                        yield scope, chunkId, float (dotProduct queryUnit vector), chunk
                            ]
                        )

                    yield! defaultArg hits []
            ]

            // Total-order sort: equal cosine scores are common (TF-IDF vectors
            // over a small corpus collide), and a score-only sort would resolve
            // ties by enumeration order and flip top-K membership run to run.
            // Tie-break on the unique `(scope key, chunk id)`.
            return
                scored
                |> List.sortBy (fun (scope, chunkId, score, _) -> -score, VectorScope.key scope, chunkId)
                |> List.truncate topK
                |> List.map (fun (scope, chunkId, score, chunk) -> {
                    ChunkId = chunkId
                    Content = chunk.Content
                    Score = score
                    Scope = scope
                    Metadata = chunk.Metadata
                })
        }

        member _.ListChunks scope includeDeleted = async {
            do! index.EnsureLoaded scope

            let chunks =
                index.Read(
                    scope,
                    fun live -> [
                        for KeyValue(chunkId, (_, chunk)) in live do
                            if includeDeleted || not (isTombstoned chunk) then
                                yield chunkId, chunk
                    ]
                )

            return defaultArg chunks []
        }

        member _.DeleteByScope scope =
            index.Mutate(
                scope,
                fun live ->
                    live.Clear()
                    (), true
            )

        member _.DeleteChunk scope chunkId =
            // A tombstone write: stamp `_deletedAt` and keep the entry. Vacuum
            // hard-removes tombstoned entries past their retention window.
            index.Mutate(
                scope,
                fun live ->
                    match live.TryGetValue chunkId with
                    | true, (vector, chunk) ->
                        let stamped = {
                            chunk with
                                Metadata =
                                    chunk.Metadata
                                    |> Map.add ChunkMetadata.DeletedAtKey (DateTimeOffset.UtcNow.ToString("o"))
                        }

                        live[chunkId] <- (vector, stamped)
                        (), true
                    | _ -> (), false
            )

        member _.RestoreChunk scope chunkId =
            index.Mutate(
                scope,
                fun live ->
                    match live.TryGetValue chunkId with
                    | true, (vector, chunk) when isTombstoned chunk ->
                        let restored = {
                            chunk with
                                Metadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey
                        }

                        live[chunkId] <- (vector, restored)
                        (), true
                    | _ -> (), false
            )

        member _.Vacuum scope olderThan =
            index.Mutate(
                scope,
                fun live ->
                    let toPurge = [
                        for KeyValue(chunkId, (_, chunk)) in live do
                            match chunk.Metadata.TryFind ChunkMetadata.DeletedAtKey with
                            | Some stamp ->
                                match DateTimeOffset.TryParse stamp with
                                | true, parsed when parsed < olderThan -> yield chunkId
                                | _ -> ()
                            | None -> ()
                    ]

                    for chunkId in toPurge do
                        live.Remove chunkId |> ignore

                    toPurge.Length, not toPurge.IsEmpty
            )

        member _.ListScopes() =
            index.ListScopes(fun live -> live.Count > 0)

        member this.Erase(scope, subjectUserId, policy, dryRun) = async {
            // An unreadable snapshot surfaces as the interface's own typed
            // failure rather than an exception out of the erasure fan-out; the
            // orchestrator retries `StoreUnreachable`.
            match!
                ToolUp.Platform.Infra.IVectorStore.eraseSubject
                    (this :> IVectorStore)
                    scope
                    subjectUserId.Value
                    policy
                    dryRun
                |> Async.Catch
            with
            | Choice1Of2 result -> return result
            | Choice2Of2(:? RagScopeSnapshotUnreadableException as ex) ->
                return Result.Error(ErasureError.StoreUnreachable("vector-store", ex.Message))
            | Choice2Of2 ex ->
                Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
                return Unchecked.defaultof<_>
        }

    interface IDisposable with
        member _.Dispose() = (index :> IDisposable).Dispose()