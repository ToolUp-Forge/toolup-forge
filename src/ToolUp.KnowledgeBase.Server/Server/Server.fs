module KnowledgeBase.Server

open Microsoft.AspNetCore.Http
open SharedTypes

// ─── KB internal helpers (extracted in Phase 15b refactor) ────────

open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerExtractors
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerIngestionObserver
open KnowledgeBase.ServerNotes
open KnowledgeBase.ServerAIContext
open KnowledgeBase.ServerInventory

// ─── KB API handler families (extracted in Phase 15b refactor) ────

open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerApiNarrative
open KnowledgeBase.ServerApiNotes
open KnowledgeBase.ServerApiAIContext

// ─── API surface composition ──────────────────────────────────────

/// Construct the Fable.Remoting `KnowledgeApi` for the current request.
/// Resolves per-request dependencies (`KnowledgeApiDeps`) once, then
/// binds each API method to its handler in `Server/Api/<X>.fs`.
let knowledgeApi (ctx: HttpContext) : KnowledgeApi =
    let deps = KnowledgeApiDeps.resolve ctx

    {
        UploadDocument = uploadDocument deps
        GetDocuments = fun () -> getDocuments deps
        DeleteDocument = deleteDocument deps
        GetStatus = getStatus deps
        IngestNarrative = ingestNarrative deps
        ResetIndex = fun () -> resetIndex deps
        AddNote = addNote deps
        UpdateNote = updateNote deps
        GetAIContext = fun () -> getAIContext deps
        SetAIContext = setAIContext deps
        GetSuggestedQuestions = getSuggestedQuestions deps
        RefreshAIContext = fun () -> refreshAIContext deps
    }

// ─── Public surface re-exports (helpers split into sibling modules) ─

/// Builds an `IIngestionStatusObserver` that reflects RAG ingestion
/// progress back into the Knowledge Base's status cache. Defined in
/// `Server/IngestionObserver.fs`; re-exported here so the public name
/// `KnowledgeBase.Server.makeIngestionStatusObserver` is preserved
/// for `composeWithRAG` wiring.
let makeIngestionStatusObserver =
    KnowledgeBase.ServerIngestionObserver.makeIngestionStatusObserver

/// Standing AI context system-prompt builder — reads the team's
/// `_ai-context.json` body for inclusion in the system prompt.
/// Defined in `Server/AIContext.fs`; re-exported here so the public
/// name `KnowledgeBase.Server.standingContextBuilder` is preserved.
let standingContextBuilder = KnowledgeBase.ServerAIContext.standingContextBuilder

/// Knowledge-base inventory system-prompt builder — emits a one-paragraph
/// summary of the index for the system prompt. Defined in
/// `Server/Inventory.fs`; re-exported here.
let kbInventoryBuilder = KnowledgeBase.ServerInventory.kbInventoryBuilder

/// Returns the canonical pair of KB system-prompt builders. Defined in
/// `Server/Inventory.fs`; re-exported here.
let knowledgeBasePromptBuilders =
    KnowledgeBase.ServerInventory.knowledgeBasePromptBuilders

/// Phase 4b — Platform Admin write surface for the Knowledge Base.
/// Mirrors `knowledgeApi` shape but writes to `VectorScope.Platform`
/// instead of the caller's team / user scope. Each write method gates
/// on `AccessContext.canModifyPlatformConfig`. Defined in
/// `Server/PlatformAdmin.fs`; re-exported here so deployments can wire
/// it as a sibling `ServerModule` alongside the team-side `knowledgeApi`.
let platformKnowledgeApi = KnowledgeBase.ServerPlatformAdmin.platformKnowledgeApi

/// Wave 1 Gap #2 — explicit operator-callable recovery hook for the KB
/// ingestion pipeline. The in-process `IngestionQueue` (in
/// `ToolUp.RAG.IngestionTypes`) has no durable backing, so a crash
/// mid-ingestion leaves the persisted `knowledge/index.json` entry in a
/// non-terminal status (Queued / ExtractingText / Embedding(n, m)) while
/// the in-flight job is gone. The KB UI keeps showing the doc as
/// progressing; retrieval misses its chunks; the user has no signal.
///
/// Call this once from the consumer's composition root *before*
/// `RAGServerApp.run`, passing the list of containers the deployment
/// manages (typically `ITeamStore.ListAll` results plus any `_platform`
/// / `_deployment` containers the consumer uses). Stuck documents are
/// marked Failed with a clear remediation reason so the KB UI surfaces a
/// badge and the user can re-upload. Defined in `Server/Recovery.fs`.
let recoverStuckDocumentsAtStartup =
    KnowledgeBase.ServerRecovery.recoverStuckDocumentsAtStartup