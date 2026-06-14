module KnowledgeBase.ServerRecovery

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes
open KnowledgeBase.ServerIndexStorage

// ─── Startup ingestion-recovery scan ──────────────────────────────
//
// The KB upload path persists per-document `IngestionStatus` to
// `knowledge/index.json` at every transition (`Queued` → `ExtractingText`
// → `Embedding(n, m)` → `Complete` | `Failed`). Status is persisted by
// the observer (`IngestionObserver.OnChunkIndexed`'s `updateIndexStatus`
// call) **and** by the upload-handler's explicit pre-enqueue
// `updateIndexStatus initialStatus` write.
//
// What survives a crash is the index entry, not the in-flight job: the
// `IngestionQueue` is a process-local `System.Threading.Channels` channel
// with no leasing or durable backing (see `IngestionTypes.IngestionQueue`'s
// XML doc + `RagConfigValidator.RagIngestionInstanceValidator`). On a
// crash mid-ingestion, the document stays in the index in a non-terminal
// status (`Queued`, `ExtractingText`, or `Embedding(n, m)`); the KB UI
// shows it as still-progressing; retrieval misses its un-indexed chunks;
// the user gets no signal beyond their own observation that "this doc
// has been ingesting for an hour".
//
// `recoverStuckDocumentsAtStartup` is the explicit operator-callable
// recovery hook. For every container the consumer enumerates, the scan:
//
// 1. Loads the persisted document index.
// 2. Filters for documents in any non-terminal status.
// 3. Writes `Failed "Ingestion was interrupted by a process restart..."`
//    back to the index so the KB UI surfaces a Failed badge with a
//    clear remediation message.
// 4. Returns the count of documents marked Failed across all containers.
//
// **Why mark Failed instead of automatically re-enqueuing.** Re-enqueue
// requires re-reading the original blob and re-running format extraction
// (PDF / DOCX / XLSX / CSV / notes / narrative). The handler-specific
// extraction step is wired into each upload entry point's
// `extractAndEnqueue` closure; re-running it from a startup hook would
// require lifting that wiring up to a shared shape — much larger scope
// than this fix targets. Marking Failed makes the gap *visible* with
// zero risk; the operator (or user) re-uploads to re-index. Auto-recovery
// (with full extractor wiring) is a follow-up phase.
//
// Containers to scan: the consumer enumerates them. Typical sources:
// `ITeamStore.ListAll` for team containers, plus the well-known platform
// (`_platform`) / deployment (`_deployment`) containers if the consumer
// uses them. The scan is idempotent — once-stuck documents that have
// since been re-uploaded carry a fresh non-Failed status and are
// passed through unchanged.

/// Compose-time recovery hook for the KB ingestion pipeline. For each
/// container in `containers`, marks every document in a non-terminal
/// status as `Failed` with a restart-interrupted reason. Returns the
/// total number of documents marked.
///
/// Run once from the consumer's composition root **before** `RAGServerApp.run`
/// (or equivalent), passing the list of containers the consumer manages.
/// Per-container errors are logged and skipped — one bad container's
/// index doesn't block scanning the rest.
let recoverStuckDocumentsAtStartup (storage: IBlobStorage) (containers: string list) (logger: ILogger) : Async<int> = async {
    let reason =
        "Ingestion was interrupted by a process restart before the document finished indexing. Re-upload the file to re-index it."

    let isNonTerminal (s: IngestionStatus) =
        match s with
        | Queued
        | ExtractingText
        | Embedding _ -> true
        | Complete _
        | Failed _
        // Phase 119 — both terminal: `UnsupportedFormat` is a stored
        // end-state, `UploadRejected` is never persisted (so never seen
        // here), but neither is a restart-stuck document to re-fail.
        | UploadRejected _
        | UnsupportedFormat _ -> false

    let mutable total = 0

    for container in containers do
        try
            let! docs = loadIndex storage container

            let stuck = docs |> List.filter (fun d -> isNonTerminal d.Status)

            if not stuck.IsEmpty then
                logger.Warn(
                    sprintf
                        "[KnowledgeBase.Recovery] container=%s — %d document(s) found in non-terminal status from a prior process; marking Failed."
                        container
                        stuck.Length
                )

                for doc in stuck do
                    do! updateIndexStatus storage container doc.Id (Failed reason)
                    total <- total + 1
        with ex ->
            logger.Error(
                sprintf
                    "[KnowledgeBase.Recovery] container=%s — recovery scan threw; skipping this container."
                    container,
                Some ex
            )

    if total > 0 then
        logger.Warn(
            sprintf
                "[KnowledgeBase.Recovery] Marked %d previously-stuck document(s) as Failed across %d container(s). Affected uploaders should see a Failed badge in the KB UI and can re-upload."
                total
                (List.length containers)
        )

    return total
}