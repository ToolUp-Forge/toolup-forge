// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.RAGVacuumJobHandler

open System
open System.Diagnostics
open System.Text
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore

// ─── Phase 14w — RAGVacuumJobHandler ──────────────────────────────
//
// `IJobHandler` registered under `VacuumHandlerName`
// (`_platform.rag.tombstone-vacuum`) and scheduled by `composeRAG` on
// a cron trigger (default daily 03:00 UTC) when a deployment opts in
// via `RAGServerApp.withVacuumSchedule`.
//
// **Why this exists.** `IVectorStore.DeleteChunk` is a tombstone write
// (Phase 14h) — it stamps `_deletedAt` and keeps the entry so
// `RestoreChunk` can un-delete within the retention window. `Vacuum`
// hard-removes tombstones past the window, but until this phase it was
// only ever driven by an operator's own admin path. A long-running
// production replica therefore accumulated soft-deleted chunks
// indefinitely and grew toward OOM. This handler drives `Vacuum` across
// every known scope on the schedule so steady-state memory stabilises
// without operator intervention.
//
// **Per-scope sweep.** On each tick the handler enumerates
// `IVectorStore.ListScopes()` and calls `Vacuum(scope, cutoff)` with
// `cutoff = now - retention` per scope. `ListScopes` on the in-memory
// default returns the scopes loaded so far — cold scopes not yet touched
// this process lifetime are vacuumed the first tick after they load, not
// before. A `KnowledgeVacuumCompleted` audit event is emitted for every
// scope that actually purged something so the sweep is queryable in the
// event trail alongside `KnowledgeChunkIndexed` / `KnowledgeChunkFailed`.
//
// **Stateless across invocations** (portability rule 4). The retention
// window is captured in `deps` at compose time (config-grade, like
// `IngestionRetryJobHandler`'s `Policy`); the payload is empty. A sweep
// that throws is `TransientFailure` so the scheduler retries on the next
// backoff — the next tick would pick up the same work regardless, so a
// transient store hiccup never drops the retention contract.

/// Logical handler name the scheduled vacuum job registers under.
/// Namespaced under `_platform.rag` like `IngestionService.RetryHandlerName`.
[<Literal>]
let VacuumHandlerName = "_platform.rag.tombstone-vacuum"

/// Default cron for `withVacuumSchedule` — daily at 03:00 UTC. Five-field
/// `Minute Hour DayOfMonth Month DayOfWeek`, the shape the in-process
/// scheduler parses (`*`, integers, comma lists, `*/N`).
[<Literal>]
let DefaultVacuumCron = "0 3 * * *"

/// Deployment-grade dependencies captured at compose time. `Retention`
/// is the tombstone window — chunks whose `_deletedAt` predates
/// `now - Retention` are hard-removed on each sweep.
type RAGVacuumDeps = {
    VectorStore: IVectorStore
    EventStore: IEventStore
    Retention: TimeSpan
    Logger: ILogger
}

/// Descriptive scope key for the audit payload — matches the
/// `InMemoryBM25Index` / vector-store convention (`platform`,
/// `deployment`, `team:{id}`).
let private scopeKey (scope: VectorScope) =
    match scope with
    | Platform -> "platform"
    | Deployment -> "deployment"
    | Team teamId -> $"team:{teamId}"

/// Audit routing scope id — where the `KnowledgeVacuumCompleted` event
/// lands so it is queryable in the right stream. Team sweeps land in the
/// team's own audit; platform/deployment sweeps land in the reserved
/// `_platform` scope.
let private auditScopeId (scope: VectorScope) =
    match scope with
    | Platform
    | Deployment -> "_platform"
    | Team teamId -> teamId

let private utf8Len (s: string) =
    if isNull s then 0 else Encoding.UTF8.GetByteCount s

/// Sum the UTF-8 `Content` bytes of chunks tombstoned before `cutoff` —
/// the bytes the imminent `Vacuum` is about to physically reclaim.
/// `Vacuum` itself returns only a purged-entry count, so the byte total
/// is measured here (over the same `_deletedAt < cutoff` predicate the
/// store applies) for the `BytesReclaimed` audit field.
let private reclaimableBytes (chunks: (string * TextChunk) list) (cutoff: DateTimeOffset) : int64 =
    chunks
    |> List.sumBy (fun (_, c) ->
        match c.Metadata.TryFind ChunkMetadata.DeletedAtKey with
        | Some ts ->
            match DateTimeOffset.TryParse ts with
            | true, parsed when parsed < cutoff -> int64 (utf8Len c.Content)
            | _ -> 0L
        | None -> 0L)

/// `IJobHandler` for `_platform.rag.tombstone-vacuum`.
type RAGVacuumJobHandler(deps: RAGVacuumDeps) =
    interface IJobHandler with
        member _.Execute(_ctx) = async {
            let cutoff = DateTimeOffset.UtcNow.Subtract deps.Retention

            try
                let! scopes = deps.VectorStore.ListScopes()

                for scope in scopes do
                    let sw = Stopwatch.StartNew()

                    // Measure reclaimable bytes before the purge — Vacuum
                    // returns only a count. `includeDeleted = true` so the
                    // tombstoned entries are in the enumeration.
                    let! chunks = deps.VectorStore.ListChunks scope true
                    let bytesReclaimed = reclaimableBytes chunks cutoff
                    let! removed = deps.VectorStore.Vacuum scope cutoff
                    sw.Stop()

                    if removed > 0 then
                        do!
                            ToolUp.RAG.IngestionService.Outcome.emitAudit
                                deps.EventStore
                                deps.Logger
                                (auditScopeId scope)
                                "KnowledgeVacuumCompleted"
                                {|
                                    ScopeKey = scopeKey scope
                                    ChunksRemoved = removed
                                    BytesReclaimed = bytesReclaimed
                                    DurationMs = sw.ElapsedMilliseconds
                                |}

                        deps.Logger.Info(
                            sprintf
                                "[RAGVacuumJobHandler] scope=%s purged %d tombstoned chunk(s), ~%d bytes reclaimed in %dms."
                                (scopeKey scope)
                                removed
                                bytesReclaimed
                                sw.ElapsedMilliseconds
                        )

                return JobResult.Success
            with ex ->
                deps.Logger.Error("[RAGVacuumJobHandler] tombstone vacuum sweep failed", Some ex)
                return JobResult.TransientFailure ex.Message
        }

/// Factory. `composeRAG` constructs the handler and registers it under
/// `VacuumHandlerName` on the resolved scheduler at startup.
let create (deps: RAGVacuumDeps) : IJobHandler =
    RAGVacuumJobHandler(deps) :> IJobHandler