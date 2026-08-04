// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerRetentionSweep

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage

// ─── Phase 512 — per-scope age-based KB retention sweep ──────────────
//
// The purge half of Phase 512 (the quota half lives at the upload
// boundary in `Api/Documents.fs`). A composed `KnowledgeRetentionPolicy`
// with a `MaxAge` expires documents whose `UploadedAt` is older than it;
// `withKnowledgeRetention` schedules `sweepJobHandler` on the composed
// `IJobScheduler` and this module does the work, one scope per run.
//
// **Derived, not stored.** Expiry reads `KnowledgeDocument.UploadedAt`
// and `SizeBytes` — fields the index has carried since the KB shipped.
// Nothing is added to the persisted record, so no pre-512 index needs
// migrating, no `FableConverters` null-coercion is required, and no
// `Fable.SimpleJson` `parseAs` on the client can start throwing on a
// field an older blob lacks.
//
// **GP 11 / GP 13 — an uncomposed deployment does nothing at all.**
// `KnowledgeRetentionPolicy.retainForever` is inert: `sweepScope`
// short-circuits before it reads storage, and `withKnowledgeRetention`
// registers no job for it, so there is no hosted service, no scheduler
// entry, and no blob read to pay for.
//
// **Phase 115 no-partial-delete contract.** Each document's retrieval
// entries are removed through `IIndexLifecycle` FIRST and its `index.json`
// entry only after that fan-out comes back clean. A document whose
// chunks did not all delete stays listed — retryable on the next sweep —
// instead of vanishing from the index while its embeddings keep serving
// retrieval. That is the same ordering `deleteDocument` uses, for the
// same reason.
//
// **GP 4.** One run reaches exactly one scope's container. The scope id
// arrives from `JobContext.ScopeId` (the scheduler's, never a caller's)
// and is mapped to a container by the same `team-` / `user-` convention
// `KnowledgeBaseLifecycle` and `KnowledgeApiDeps.resolve` use.

/// Outcome of one scope's sweep. Returned by `sweepScope` so the job
/// handler, the tests, and an operator-triggered run all read the same
/// evidence rather than inferring it from logs.
type RetentionSweepReport = {
    /// Scope the sweep ran against.
    ScopeId: string
    /// Container it resolved to.
    Container: string
    /// Documents the scope held when the sweep started.
    Examined: int
    /// Ids actually removed — index entry, raw blob, and retrieval
    /// entries all gone.
    Purged: string list
    /// Total `SizeBytes` across `Purged`.
    ReclaimedBytes: int64
    /// Chunks that survived the retrieval-index fan-out. Non-zero means
    /// a document was left listed for retry (GP 9).
    OrphanChunkCount: int
    /// Operator-legible failures — one per document whose fan-out was
    /// not clean, plus any storage-level error.
    Failures: string list
}

module RetentionSweepReport =
    /// A run that did nothing: no policy, no expired documents, or an
    /// unresolvable scope.
    let noOp (scopeId: string) (container: string) (examined: int) : RetentionSweepReport = {
        ScopeId = scopeId
        Container = container
        Examined = examined
        Purged = []
        ReclaimedBytes = 0L
        OrphanChunkCount = 0
        Failures = []
    }

    /// `true` when every expired document was removed cleanly.
    let isClean (report: RetentionSweepReport) : bool =
        report.Failures.IsEmpty && report.OrphanChunkCount = 0

    /// One-line operator summary.
    let summarise (report: RetentionSweepReport) : string =
        sprintf
            "scope %s: examined %d, purged %d (%d bytes reclaimed)%s"
            report.ScopeId
            report.Examined
            report.Purged.Length
            report.ReclaimedBytes
            (if isClean report then
                 ""
             else
                 sprintf
                     ", %d orphan chunk(s), %d failure(s): %s"
                     report.OrphanChunkCount
                     report.Failures.Length
                     (String.concat "; " report.Failures))

/// Resolve the KB blob container for a scope id, mirroring the
/// `team-{id}` / `user-{id}` convention `KnowledgeApiDeps.resolve` and
/// `KnowledgeBaseLifecycle` already use. An already-prefixed id is taken
/// verbatim; a bare id is a team id (the tenant unit).
let containerOf (scopeId: string) : string =
    if
        scopeId.StartsWith("user-", StringComparison.Ordinal)
        || scopeId.StartsWith("team-", StringComparison.Ordinal)
    then
        scopeId
    else
        "team-" + scopeId

/// Vector scope for a container, mirroring `KnowledgeApiDeps.resolve`
/// exactly: a `team-` container is the team-shared vector scope, anything
/// else is that user's own. `Deployment` is never synthesised here — it
/// is reserved for genuinely deployment-wide shared content, and routing
/// a per-user purge at it would delete another caller's chunks.
let vectorScopeOf (container: string) : VectorScope =
    if container.StartsWith("team-", StringComparison.Ordinal) then
        Team(container.Substring 5)
    else if container.StartsWith("user-", StringComparison.Ordinal) then
        User(container.Substring 5)
    else
        User container

/// Per-scope content-hash dedup index (Phase 14x), rebuilt here so a
/// purged document's hash ref is dropped alongside its index entry — a
/// later re-upload of the same bytes then re-ingests instead of
/// deduplicating onto a document retention removed.
let private contentHashIndex (storage: IBlobStorage) (container: string) : SecondaryIndex.BlobIndex<string, string> =
    SecondaryIndex.BlobIndex.create storage container "_platform/kb-content-hash" id id Some

/// Sweep one scope: purge every document past `policy.MaxAge` whose
/// source kind is in scope, through the Phase 115 deletion fan-out, and
/// emit one `KnowledgeDocumentsPurged` audit row when anything was
/// removed. Idempotent — a second run finds nothing expired and is a
/// no-op. `now` is a parameter rather than a `DateTimeOffset.UtcNow`
/// read so the selection is testable without waiting.
let sweepScope
    (storage: IBlobStorage)
    (lifecycle: IIndexLifecycle.IIndexLifecycle option)
    (auditLog: IAuditLog option)
    (logger: ILogger)
    (now: DateTimeOffset)
    (policy: KnowledgeRetentionPolicy)
    (scopeId: string)
    : Async<RetentionSweepReport> =
    async {
        let container = containerOf scopeId

        // GP 11 / GP 13 — an inert policy reads nothing. This guard is
        // what makes "no policy ⇒ no purge" a structural property rather
        // than an emergent one: there is no code path from here to a
        // delete when `MaxAge` is `None`.
        if KnowledgeRetentionPolicy.isInert policy then
            return RetentionSweepReport.noOp scopeId container 0
        else
            let! index = loadIndex storage container
            let expired = KnowledgeRetentionPolicy.selectExpired now policy index

            if expired.IsEmpty then
                return RetentionSweepReport.noOp scopeId container index.Length
            else
                let vectorScope = vectorScopeOf container
                let hashIndex = contentHashIndex storage container

                // Remove ONE expired document. `Ok doc` when its retrieval
                // entries, raw blob and hash ref all went; `Error (orphans,
                // reason)` when the Phase 115 fan-out was not clean — the
                // caller then leaves it listed so the next sweep retries it.
                let purgeOne (doc: KnowledgeDocument) : Async<Result<KnowledgeDocument, int * string>> = async {
                    let! report =
                        match lifecycle with
                        | Some l -> l.DeleteDocument vectorScope doc.Id doc.ChunkCount
                        | None -> async.Return IIndexLifecycle.IndexLifecycleReport.empty

                    if IIndexLifecycle.IndexLifecycleReport.isClean report then
                        let rawBlobName = sprintf "knowledge/%s/%s" doc.Id doc.FileName
                        let! _ = storage.Delete(container, rawBlobName)

                        match doc.ContentHash with
                        | Some hash -> do! hashIndex.Remove hash doc.Id
                        | None -> ()

                        statusCache.TryRemove doc.Id |> ignore
                        progressCache.TryRemove doc.Id |> ignore
                        return Ok doc
                    else
                        let survivors = IIndexLifecycle.IndexLifecycleReport.summarise report

                        logger.Warn(
                            sprintf
                                "[KnowledgeBase] Retention sweep left %s in scope %s — %s. The document stays listed and the next sweep retries it."
                                doc.Id
                                scopeId
                                survivors
                        )

                        return
                            Error(report.SurvivingChunkIds.Length, sprintf "%s (%s) — %s" doc.Id doc.FileName survivors)
                }

                // Sequential by design: the fan-out mutates shared
                // retrieval indexes and `index.json` is rewritten once at
                // the end, so concurrency here would buy little and race
                // the container lock.
                let rec purgeEach
                    (remaining: KnowledgeDocument list)
                    (acc: Result<KnowledgeDocument, int * string> list)
                    : Async<Result<KnowledgeDocument, int * string> list> =
                    async {
                        match remaining with
                        | [] -> return List.rev acc
                        | doc :: rest ->
                            let! outcome = purgeOne doc
                            return! purgeEach rest (outcome :: acc)
                    }

                let! outcomes = purgeEach expired []

                let purgedDocs = outcomes |> List.choose Result.toOption

                let failedOutcomes =
                    outcomes
                    |> List.choose (function
                        | Error e -> Some e
                        | Ok _ -> None)

                let orphanChunks = failedOutcomes |> List.sumBy fst
                let failures = failedOutcomes |> List.map snd
                let purgedIds = purgedDocs |> List.map _.Id
                let purgedIdSet = Set.ofList purgedIds

                if not purgedIds.IsEmpty then
                    let survivorsIndex = index |> List.filter (fun d -> not (purgedIdSet.Contains d.Id))
                    do! saveIndex storage container survivorsIndex
                    KnowledgeBase.ServerInventory.invalidateInventoryCache container

                let reclaimed = purgedDocs |> List.sumBy _.SizeBytes

                let maxAgeSeconds =
                    policy.MaxAge
                    |> Option.map (fun a -> int64 a.TotalSeconds)
                    |> Option.defaultValue 0L

                // GP 6 — one row per run that removed something. A run
                // that expired nothing writes no row: a purge trail
                // records deletions, not the absence of them.
                if not purgedIds.IsEmpty then
                    match auditLog with
                    | Some audit ->
                        do!
                            audit.Record(
                                scopeId,
                                KnowledgeDocumentsPurged {
                                    ScopeId = scopeId
                                    DocumentIds = purgedIds
                                    PurgedCount = purgedIds.Length
                                    ReclaimedBytes = reclaimed
                                    MaxAgeSeconds = maxAgeSeconds
                                    OrphanChunkCount = orphanChunks
                                }
                            )
                    | None -> ()

                    logger.Info(
                        sprintf
                            "[KnowledgeBase] Retention sweep purged %d document(s) from scope %s (%d bytes reclaimed)."
                            purgedIds.Length
                            scopeId
                            reclaimed
                    )

                return {
                    ScopeId = scopeId
                    Container = container
                    Examined = index.Length
                    Purged = purgedIds
                    ReclaimedBytes = reclaimed
                    OrphanChunkCount = orphanChunks
                    Failures = failures
                }
    }

/// Handler name the sweep job registers under. Namespaced against the
/// companion so it cannot clash with a consumer module's own handlers.
[<Literal>]
let SweepHandlerName = "knowledge-base.retention-sweep"

/// The `IJobHandler` `withKnowledgeRetention` registers. Resolves its
/// substrate from the provider on every `Execute` — nothing is cached
/// between invocations (GP 12 rule 4), so a distributed scheduler may
/// deactivate and rehydrate it freely. Sweeps `ctx.ScopeId` only.
///
/// A sweep that leaves orphans returns `TransientFailure`: the shape is
/// retryable by construction (the next attempt re-runs the same
/// fan-out over the documents that stayed listed), which is exactly
/// what `JobRetryPolicy` backoff exists for. A hard throw out of the
/// storage layer is likewise transient — the scheduler catches it, but
/// returning it explicitly keeps the run's error text useful.
type RetentionSweepJobHandler(services: IServiceProvider, policy: KnowledgeRetentionPolicy) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            let logger =
                match services.GetService typeof<ILogger> with
                | :? ILogger as l -> l
                | _ ->
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

            match services.GetService typeof<IBlobStorage> with
            | :? IBlobStorage as storage ->
                let lifecycle =
                    match services.GetService typeof<IIndexLifecycle.IIndexLifecycle> with
                    | :? IIndexLifecycle.IIndexLifecycle as l -> Some l
                    | _ -> None

                let auditLog =
                    match services.GetService typeof<IAuditLog> with
                    | :? IAuditLog as a -> Some a
                    | _ -> None

                try
                    let! report = sweepScope storage lifecycle auditLog logger DateTimeOffset.UtcNow policy ctx.ScopeId

                    if RetentionSweepReport.isClean report then
                        return Success
                    else
                        return TransientFailure(RetentionSweepReport.summarise report)
                with ex ->
                    logger.Error(sprintf "[KnowledgeBase] Retention sweep failed for scope %s" ctx.ScopeId, Some ex)

                    return TransientFailure ex.Message
            | _ ->
                // No blob storage composed — the KB substrate writes
                // through `IBlobStorage`, so there is nothing to
                // sweep. Permanent: a retry cannot conjure a store.
                return
                    PermanentFailure
                        "No IBlobStorage is registered; the Knowledge Base retention sweep has nothing to sweep."
        }