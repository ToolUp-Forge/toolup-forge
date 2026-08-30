module KnowledgeBase.ServerRecovery

open System
open Microsoft.Extensions.DependencyInjection
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
// What survives a crash is the index entry, not the in-flight job: on
// the in-memory default the `IngestionQueue` is a process-local
// `System.Threading.Channels` channel with nothing to redeliver from
// (see `IngestionTypes.IngestionQueue`'s XML doc +
// `RagConfigValidator.RagIngestionInstanceValidator`). On a crash
// mid-ingestion, the document stays in the index in a non-terminal
// status (`Queued`, `ExtractingText`, or `Embedding(n, m)`); the KB UI
// shows it as still-progressing; retrieval misses its un-indexed chunks;
// the user gets no signal beyond their own observation that "this doc
// has been ingesting for an hour".
//
// **Phase 723 — this is no longer a second sweep.** The traversal, the
// per-scope error isolation, the reason string and the logging shape now
// live once in `ToolUp.Platform.IngestionRecoverySweep`, shared with the
// RAG / Data-Manager status sweep that used to duplicate them. What
// stays here is the part that is genuinely KB's: an
// `IIngestionRecoverySurface` adapter over the `knowledge/index.json`
// document index, and its own answer to "which statuses are
// non-terminal".
//
// The two sweeps could not become one TRAVERSAL, and that is a finding
// rather than a shortfall: they read two different durable surfaces —
// KB's document index and RAG's per-file `IIngestionStatusStore` — and a
// KB deployment holds BOTH. Sweeping only one would leave the other's
// badge stuck exactly as before. Converging them onto one implementation
// with two adapters is the whole of the available duplication; a third
// surface is now an adapter, not a third sweep.
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
// Which scopes are swept: since Phase 723 a deployment composing
// `RAGServerApp.withScopeEnumerator` gets them enumerated (the default
// adapts `ITeamStore.ListTeams` plus the well-known `_platform` /
// `_deployment` containers) and needs no hand-written list at all. The
// explicit-list form below remains for deployments that enumerate their
// own scopes. The scan is idempotent either way — once-stuck documents
// that have since been re-uploaded carry a fresh non-Failed status and
// are passed through unchanged.

/// Is this a status a dead process could have abandoned mid-ingestion?
///
/// Kept a named function rather than inlined into the surface, because
/// which statuses are terminal is the one KB-specific judgement in the
/// whole sweep and every future status case has to be classified here.
let private isNonTerminal (s: IngestionStatus) =
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
    | UnsupportedFormat _
    // Phase 500 — also terminal: the document was stored and its
    // content needs an OCR companion this deployment does not have.
    // Re-failing it on restart would replace an accurate, actionable
    // status with a generic one.
    | OcrUnavailable _ -> false

/// Phase 723 — the KB document index as an `IIngestionRecoverySurface`,
/// so the shared `IngestionRecoverySweep` can visit it alongside the
/// RAG per-file status store.
let documentIndexSurface (storage: IBlobStorage) : IIngestionRecoverySurface =
    { new IIngestionRecoverySurface with
        member _.Name = "knowledge-base-index"

        member _.ListInterrupted(scope) = async {
            let! docs = loadIndex storage scope

            return docs |> List.filter (fun d -> isNonTerminal d.Status) |> List.map _.Id
        }

        member _.MarkInterrupted(scope, documentId, reason) =
            updateIndexStatus storage scope documentId (Failed reason)
    }

/// Compose-time recovery hook for the KB ingestion pipeline. For each
/// container in `containers`, marks every document in a non-terminal
/// status as `Failed` with a restart-interrupted reason. Returns the
/// total number of documents marked.
///
/// Run once from the consumer's composition root **before** `RAGServerApp.run`
/// (or equivalent), passing the list of containers the consumer manages.
/// Per-container errors are logged and skipped — one bad container's
/// index doesn't block scanning the rest.
///
/// Phase 723 — kept as the explicit, operator-callable form; a
/// deployment that composes `KnowledgeBaseServerApp.withIngestionRecovery`
/// plus a scope enumerator gets the same sweep run automatically at
/// startup over enumerated scopes, and does not call this at all.
let recoverStuckDocumentsAtStartup (storage: IBlobStorage) (containers: string list) (logger: ILogger) : Async<int> =
    IngestionRecoverySweep.run [ documentIndexSurface storage ] containers logger

/// Append a service registration onto the shared `ComposeExtensions`
/// seam — the same threading every other KB compose hook uses.
let private withServiceConfig (register: IServiceCollection -> IServiceCollection) (app: ServerApp) : ServerApp = {
    app with
        Extensions = {
            app.Extensions with
                ServiceConfig =
                    match app.Extensions.ServiceConfig with
                    | None -> Some register
                    | Some baseFn -> Some(fun s -> register (baseFn s))
        }
}

/// Phase 723 — enrol the KB document index in the startup
/// restart-recovery sweep.
///
/// Registers the `IIngestionRecoverySurface` adapter as a DI singleton.
/// The sweep itself is the hosted service `composeWithRAG` registers, so
/// this composes in either order with `withScopeEnumerator` /
/// `withIngestionRecoverySweep` and needs no wiring of its own — the
/// hosted service resolves every registered surface at `StartAsync`.
///
/// **Registering this alone sweeps nothing** (GP 11 / GP 13). The
/// hosted service exists only when the deployment also composed a scope
/// enumerator or an explicit container list, so a deployment that adds
/// this line and nothing else is byte-for-byte its prior self. That is
/// deliberate: a surface registration is a declaration that KB's index
/// SHOULD be swept, never an instruction to start rewriting document
/// statuses at the next restart.
///
/// The storage handle is resolved from the built provider on each run
/// rather than captured here, so nothing is held between invocations
/// (GP 12 rule 4).
let withIngestionRecovery (app: ServerApp) : ServerApp =
    app
    |> withServiceConfig (fun s ->
        s.AddSingleton<IIngestionRecoverySurface>(
            Func<IServiceProvider, IIngestionRecoverySurface>(fun sp ->
                documentIndexSurface (sp.GetRequiredService<IBlobStorage>()))
        ))