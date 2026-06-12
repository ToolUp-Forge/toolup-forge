// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.IIndexLifecycle

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingCache

// ─── Phase 115 — unified index-lifecycle seam ─────────────────────────
//
// One fan-out seam for delete / erase across every retrieval index, so
// producers (KnowledgeBase is the canonical one) structurally *cannot*
// reach the vector store and miss the sparse index. The 2026-06-12
// Investigate-gaps pass found exactly that class of defect three times
// over: every KB deletion path looped `IVectorStore.DeleteChunk` only,
// while `composeWithRAG` unconditionally fuses a BM25 sparse index that
// stores full chunk text and persists it to blob storage — deleted and
// data-subject-erased content kept surfacing through the hybrid sparse
// leg into AI answers, and remained at rest in `bm25.json`.
//
// Phase 9c portability rules:
// 1. Identity by value — `chunkId: string`, `docId: string`,
//    `scope: VectorScope`.
// 2. Async at every boundary.
// 3. Failure reporting as data (`IndexLifecycleReport`) — no callback /
//    supervision hooks.
// 4. Stateless between calls.
// 5. No cross-scope ordering promises.
// 6. Precision: best-effort fan-out; per-target completion is reported,
//    never assumed.

/// Aggregate outcome of a lifecycle fan-out. Per-target failure
/// isolation is the seam's core contract: one store throwing must not
/// abort the others, and the caller learns exactly what survived so a
/// partial delete is loud and retryable rather than silently
/// half-complete (GP 9).
type IndexLifecycleReport = {
    /// Targets that completed without error this call
    /// (`"vector-store"`, `"sparse-index"`, `"embedding-cache"`).
    Succeeded: string list
    /// `(targetName, error)` per failing target-level operation.
    Failures: (string * string) list
    /// `(targetName, chunkId)` per chunk that could not be deleted —
    /// populated by the per-chunk operations (`DeleteDocument`), empty
    /// for scope-level ones.
    SurvivingChunkIds: (string * string) list
}

module IndexLifecycleReport =
    let empty = {
        Succeeded = []
        Failures = []
        SurvivingChunkIds = []
    }

    /// `true` when every target completed and no chunk survived.
    let isClean (report: IndexLifecycleReport) =
        report.Failures.IsEmpty && report.SurvivingChunkIds.IsEmpty

    /// One-line operator summary for survivor logging.
    let summarise (report: IndexLifecycleReport) =
        if isClean report then
            "clean"
        else
            let failures =
                report.Failures
                |> List.map (fun (target, err) -> sprintf "%s: %s" target err)
                |> String.concat "; "

            let survivors =
                report.SurvivingChunkIds
                |> List.groupBy fst
                |> List.map (fun (target, chunks) -> sprintf "%s: %d chunk(s)" target chunks.Length)
                |> String.concat "; "

            [
                if failures <> "" then
                    sprintf "failures [%s]" failures
                if survivors <> "" then
                    sprintf "survivors [%s]" survivors
            ]
            |> String.concat " "

/// Unified deletion / erasure surface over every retrieval index a
/// deployment composes — vector store, sparse (lexical) index, and the
/// embedding cache. Producers call this instead of reaching for
/// `IVectorStore` directly, so a new index tier added to the platform
/// is one new fan-out target here rather than N call-site sweeps.
type IIndexLifecycle =
    /// Delete one chunk from every index. Tombstone semantics on the
    /// vector store (per its contract), hard delete on the sparse
    /// index.
    abstract DeleteChunk: scope: VectorScope -> chunkId: string -> Async<IndexLifecycleReport>

    /// Delete every chunk of a document from every index, using the
    /// platform ingestion chunk-id convention `{docId}:chunk:{i}` for
    /// `i in 0 .. chunkCount - 1` (the scheme the KB upload / note /
    /// narrative ingestion paths write). Per-chunk + per-target failure
    /// isolation: one failing chunk delete never aborts the rest;
    /// survivors come back in the report so the caller can keep the
    /// document listed and retryable.
    abstract DeleteDocument: scope: VectorScope -> docId: string -> chunkCount: int -> Async<IndexLifecycleReport>

    /// Delete every chunk in the scope from every index, including
    /// persisted snapshots. The embedding cache is content-hash keyed
    /// (no per-scope partition), so it is not touched here — see
    /// `Erase` for the privacy-relevant path that flushes it.
    abstract DeleteByScope: scope: VectorScope -> Async<IndexLifecycleReport>

    /// Data-subject erasure across every index: vector store
    /// (`IVectorStore.Erase` matching), sparse index
    /// (`ISparseIndex.Erase`, same matching contract), and an embedding
    ///-cache flush (hash-keyed, so a full flush is the
    /// privacy-correct response). Summaries are merged; a failing
    /// target yields `Error` so the DSR orchestration records the
    /// incomplete run.
    abstract Erase:
        scope: VectorScope * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>

// ─── Default implementation ──────────────────────────────────────────

[<Literal>]
let private vectorTarget = "vector-store"

[<Literal>]
let private sparseTarget = "sparse-index"

[<Literal>]
let private cacheTarget = "embedding-cache"

/// Default `IIndexLifecycle` over whichever index tiers the deployment
/// composed. `sparseIndex` / `embeddingCache` are optional so a
/// vector-only deployment (no RAG hybrid leg) gets the same seam with
/// fewer targets — KB code then has exactly one deletion path
/// regardless of composition.
type DefaultIndexLifecycle
    (
        vectorStore: IVectorStore,
        sparseIndex: ISparseIndex option,
        embeddingCache: IEmbeddingCache option,
        ?logger: ILogger
    ) =

    /// Run one target's operation with failure isolation; fold the
    /// outcome into the report.
    let runTarget (target: string) (operation: Async<unit>) (report: IndexLifecycleReport) = async {
        try
            do! operation

            return {
                report with
                    Succeeded = target :: report.Succeeded
            }
        with ex ->
            return {
                report with
                    Failures = (target, ex.Message) :: report.Failures
            }
    }

    let logIfDirty (operation: string) (report: IndexLifecycleReport) =
        if not (IndexLifecycleReport.isClean report) then
            match logger with
            | Some log ->
                log.Warn(
                    sprintf
                        "[IndexLifecycle] %s completed partially — %s. The caller keeps the affected entries listed/retryable; nothing is silently orphaned."
                        operation
                        (IndexLifecycleReport.summarise report)
                )
            | None -> ()

    interface IIndexLifecycle with
        member _.DeleteChunk scope chunkId = async {
            let! report = runTarget vectorTarget (vectorStore.DeleteChunk scope chunkId) IndexLifecycleReport.empty

            let! report =
                match sparseIndex with
                | Some sparse -> runTarget sparseTarget (sparse.DeleteChunk scope chunkId) report
                | None -> async.Return report

            logIfDirty (sprintf "DeleteChunk %s" chunkId) report
            return report
        }

        member _.DeleteDocument scope docId chunkCount = async {
            // Per-chunk + per-target isolation. Survivors are recorded
            // by chunk id so the caller's log pinpoints exactly what to
            // retry — mirrors the 0.4.3 narrative-ingestion hardening.
            let mutable failures: (string * string) list = []
            let mutable survivors: (string * string) list = []
            let mutable vectorOk = true
            let mutable sparseOk = sparseIndex.IsSome

            for i in 0 .. chunkCount - 1 do
                let chunkId = sprintf "%s:chunk:%d" docId i

                try
                    do! vectorStore.DeleteChunk scope chunkId
                with ex ->
                    vectorOk <- false
                    failures <- (vectorTarget, ex.Message) :: failures
                    survivors <- (vectorTarget, chunkId) :: survivors

                match sparseIndex with
                | Some sparse ->
                    try
                        do! sparse.DeleteChunk scope chunkId
                    with ex ->
                        sparseOk <- false
                        failures <- (sparseTarget, ex.Message) :: failures
                        survivors <- (sparseTarget, chunkId) :: survivors
                | None -> ()

            let report = {
                Succeeded = [
                    if vectorOk then
                        vectorTarget
                    if sparseIndex.IsSome && sparseOk then
                        sparseTarget
                ]
                // One representative failure per target keeps the report
                // bounded on a large half-failing document; the survivor
                // list still carries every affected chunk id.
                Failures = failures |> List.distinctBy fst
                SurvivingChunkIds = List.rev survivors
            }

            logIfDirty (sprintf "DeleteDocument %s (%d chunks)" docId chunkCount) report
            return report
        }

        member _.DeleteByScope scope = async {
            let! report = runTarget vectorTarget (vectorStore.DeleteByScope scope) IndexLifecycleReport.empty

            let! report =
                match sparseIndex with
                | Some sparse -> runTarget sparseTarget (sparse.DeleteByScope scope) report
                | None -> async.Return report

            logIfDirty "DeleteByScope" report
            return report
        }

        member _.Erase(scope, subjectUserId, policy, dryRun) = async {
            let! vectorResult = vectorStore.Erase(scope, subjectUserId, policy, dryRun)

            let! sparseResult =
                match sparseIndex with
                | Some sparse -> sparse.Erase(scope, subjectUserId, policy, dryRun)
                | None ->
                    async.Return(
                        Result.Ok {
                            HandlerName = sparseTarget
                            RecordsAffected = 0
                            Note = Some "no sparse index composed"
                        }
                    )

            // Flush derived embeddings so an erased chunk's vector can't
            // be served from cache afterwards. Hash-keyed cache — a full
            // flush is the privacy-correct (and only possible) response.
            // Skipped on dryRun: previews must not cost the cache.
            if not dryRun then
                match embeddingCache with
                | Some cache -> do! cache.Clear()
                | None -> ()

            return
                match vectorResult, sparseResult with
                | Result.Ok v, Result.Ok s ->
                    Result.Ok {
                        HandlerName = "index-lifecycle"
                        RecordsAffected = v.RecordsAffected + s.RecordsAffected
                        Note =
                            let parts =
                                [
                                    v.Note |> Option.map (sprintf "%s: %s" vectorTarget)
                                    s.Note |> Option.map (sprintf "%s: %s" sparseTarget)
                                ]
                                |> List.choose id

                            if parts.IsEmpty then
                                None
                            else
                                Some(String.concat "; " parts)
                    }
                | Result.Error err, _ -> Result.Error err
                | _, Result.Error err -> Result.Error err
        }