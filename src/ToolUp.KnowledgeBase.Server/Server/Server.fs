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
        GetOriginalDocument = getOriginalDocument deps
        GetScopeUsage = fun () -> getScopeUsage deps
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

/// Phase 104 — register a custom `IOriginalSourceResolver` so a
/// deployment can extend original-document resolution to a custom
/// source kind (or rewire the built-in per-`KnowledgeSource` branches).
/// Defined in `Server/OriginalSourceResolver.fs`; re-exported here so
/// the public name `KnowledgeBase.Server.withOriginalSourceResolver`
/// sits alongside the other compose-time hooks. Apps that never call
/// this get the default resolver (UploadedFile → raw blob, Note →
/// note.md markdown, narrative → `NoOriginalAvailable`).
let withOriginalSourceResolver =
    KnowledgeBase.ServerOriginalSourceResolver.withOriginalSourceResolver

/// Phase 200 — register an `IOriginalPreviewSeam` so a deployment can
/// serve "open the original at the cited spot, highlighted" previews:
/// a resolved original (inline, or behind a time-bound signed URL)
/// paired with a neutral `PreviewAnchor` projected from the citation's
/// `SourceLocator`. The SDK ships no viewer (GP 1) — only the seam and
/// the anchor contract. Defined in `Server/IOriginalPreviewSeam.fs`;
/// re-exported here so the public name
/// `KnowledgeBase.Server.withOriginalPreviewSeam` sits alongside the
/// other compose-time hooks. Apps that never call this register
/// nothing and keep the pre-200 original-retrieval path byte-for-byte
/// (GP 13).
let withOriginalPreviewSeam =
    KnowledgeBase.ServerOriginalPreviewSeam.withOriginalPreviewSeam

/// Phase 119 — compose a Knowledge Base upload policy: a `MaxUploadBytes`
/// size cap, an `AllowedExtensions` type allowlist, and how to treat an
/// upload whose type no extractor recognises (`Reject` vs
/// `AcceptUnindexed`). Filename sanitisation (`../../index.json` →
/// `index.json` under a server-controlled key) is always applied at the
/// upload boundary regardless of policy. Defined in `Server/UploadPolicy.fs`;
/// re-exported here alongside the other compose-time hooks. Apps that
/// never call this get `KnowledgeUploadPolicy.permissive` (no caps;
/// pre-119 behaviour modulo sanitisation + the `UnsupportedFormat` status
/// fix).
let withUploadPolicy = KnowledgeBase.ServerUploadPolicy.withUploadPolicy

/// Phase 14x — compose-time lever for KB upload content-hash dedup
/// (on by default; `withDocumentDedup false` restores the pre-14x
/// always-ingest behaviour byte-for-byte). Defined in
/// `Server/UploadPolicy.fs`; re-exported here alongside the other
/// compose-time hooks.
let withDocumentDedup = KnowledgeBase.ServerUploadPolicy.withDocumentDedup

/// Phase 512 — compose a per-scope **corpus** quota (`MaxDocuments` /
/// `MaxBytes`), enforced at the upload boundary before anything is
/// persisted. Complements `withUploadPolicy`, which caps one upload;
/// this caps the accumulated corpus. Apps that never call this get
/// `KnowledgeQuotaPolicy.unlimited` — no tally, no refusal, pre-512
/// behaviour byte-for-byte. Defined in `Server/UploadPolicy.fs`.
let withKnowledgeQuota = KnowledgeBase.ServerUploadPolicy.withKnowledgeQuota

/// Phase 512 — mount the `EnableDevEndpoints`-gated
/// `/dev/knowledge-base/usage` diagnostics endpoint reporting the
/// caller's scope usage against the composed quota. Defined in
/// `Server/UploadPolicy.fs`.
let withKnowledgeUsageEndpoint =
    KnowledgeBase.ServerUploadPolicy.withKnowledgeUsageEndpoint

/// Phase 512 — the common pairing: `withKnowledgeQuota` +
/// `withKnowledgeUsageEndpoint`. Defined in `Server/UploadPolicy.fs`.
let withKnowledgeQuotaAndUsageEndpoint =
    KnowledgeBase.ServerUploadPolicy.withKnowledgeQuotaAndUsageEndpoint

/// Phase 512 — compose per-scope age-based retention and schedule the
/// purge sweep on the composed `IJobScheduler` for the given scopes.
/// `KnowledgeRetentionPolicy.retainForever` (or an empty scope list)
/// registers no job at all, so an uncomposed deployment purges nothing
/// and pays nothing. The scope list is explicit for the same reason
/// `recoverStuckDocumentsAtStartup` takes one — `IBlobStorage` has no
/// cross-container enumeration. Defined in `Server/UploadPolicy.fs`;
/// the sweep itself is `Server/RetentionSweep.fs`.
let withKnowledgeRetention = KnowledgeBase.ServerUploadPolicy.withKnowledgeRetention

/// Phase 512 — sweep ONE scope's expired documents immediately, outside
/// the scheduler. The operator-callable form of the retention job (and
/// what the contract tests drive): same fan-out, same audit row, `now`
/// supplied by the caller so the selection is deterministic. Defined in
/// `Server/RetentionSweep.fs`.
let sweepExpiredDocuments = KnowledgeBase.ServerRetentionSweep.sweepScope

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