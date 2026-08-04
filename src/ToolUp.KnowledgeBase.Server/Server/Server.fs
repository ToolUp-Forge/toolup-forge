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

    // Phase 200 / 108 — the original-preview seam, resolved here rather
    // than on `KnowledgeApiDeps` because exactly one handler consumes
    // it. Unlike the Phase 104 resolver there is no default: an absent
    // registration IS the opt-out, and `getOriginalDelivery` then runs
    // the Phase 102 path unchanged.
    let previewSeam =
        match ctx.RequestServices.GetService(typeof<KnowledgeBase.ServerOriginalPreviewSeam.IOriginalPreviewSeam>) with
        | :? KnowledgeBase.ServerOriginalPreviewSeam.IOriginalPreviewSeam as s -> Some s
        | _ -> None

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
        GetOriginalDelivery = getOriginalDelivery previewSeam deps
        GetScopeUsage = fun () -> getScopeUsage deps
        GetDocumentVersions = getDocumentVersions deps
        ImportBatch = importBatch deps
        SetDocumentTags = setDocumentTags deps
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

/// Phase 108 — opt into time-bound direct-download URLs for originals.
/// `GetOriginalDelivery` then returns a short-lived signed URL instead
/// of the bytes, on any composed `IBlobStorage` that can mint one
/// (Azure / S3 / GCS), and transparently proxies on any that cannot
/// (local filesystem, encrypted-at-rest). Defined in
/// `Server/IOriginalPreviewSeam.fs`; re-exported here so the public
/// name `KnowledgeBase.Server.withSignedOriginalUrls` sits alongside
/// the other compose-time hooks. Apps that never call this keep the
/// Phase 102 proxy path byte-for-byte (GP 11 / GP 13).
let withSignedOriginalUrls =
    KnowledgeBase.ServerOriginalPreviewSeam.withSignedOriginalUrls

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

/// Phase 510 — compose-time lever for KB upload **versioning** (OFF by
/// default). With it, a re-upload of an edited file under a name the
/// scope already holds supersedes that document in place: one document,
/// a version chain, prior versions preserved, only changed chunks
/// re-embedded, and no orphan chunk tail left behind by a shorter
/// revision. Without it the pre-510 path is byte-for-byte intact.
/// Defined in `Server/UploadPolicy.fs`; re-exported here alongside the
/// other compose-time hooks.
let withDocumentVersioning = KnowledgeBase.ServerUploadPolicy.withDocumentVersioning

/// Phase 105 — compose-time lever for retaining KB **originals** in
/// `IDataObjectStore` instead of at the raw `knowledge/{docId}/{name}`
/// blob convention (OFF by default). With it, originals gain
/// content-addressable dedup at rest, a metadata envelope, the store's
/// version chain, and — because `Save` records `createdBy` — coverage
/// by the Phase 9h data-subject `Erase` surface, which a raw blob never
/// had. Reads fall back to the convention path, so pre-opt-in documents
/// stay retrievable with no backfill. Defined in `Server/UploadPolicy.fs`.
let withObjectStoreRetention =
    KnowledgeBase.ServerUploadPolicy.withObjectStoreRetention

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

/// Phase 511 — override the bulk-import archive-expansion guards
/// (entry count, per-entry bytes, total uncompressed bytes, compression
/// ratio). Apps that never call this get `ArchiveImportPolicy.defaults`,
/// which already carries real caps — the one KB policy whose uncomposed
/// default is not permissive, because a new archive-expansion surface
/// with no bomb guards would be a defect dressed as a default. The
/// single-file upload path never reads it (GP 11). Defined in
/// `Server/BulkImport.fs`.
let withArchiveImportPolicy = KnowledgeBase.ServerBulkImport.withArchiveImportPolicy

/// Phase 511 — enable fetch-by-URL ingestion for an explicit host
/// allowlist, and register the BCL `HttpClient` transport that performs
/// it. **Not calling this leaves URL ingestion inert**: with no policy
/// registered the handler resolves `UrlIngestionPolicy.disabled`, whose
/// allowlist is empty, and the gate refuses before it even parses a URL.
/// There is no wildcard and no enable-flag separate from the allowlist,
/// so the only way to reach the network is to name a host; composing an
/// empty allowlist is therefore also inert, which is how a deployment
/// that wires this from configuration fails closed. Redirects are
/// re-gated per hop, literal IP hosts are refused outright, and only
/// http/https are fetchable. Defined in `Server/BulkImport.fs`.
let withUrlIngestion = KnowledgeBase.ServerBulkImport.withUrlIngestion

/// Phase 511 — substitute the URL transport (an egress proxy, a
/// signed-fetch service) without changing allowlist semantics: the gate
/// stays in `classifyUrl`, only the bytes arrive differently. Defined in
/// `Server/BulkImport.fs`.
let withUrlContentFetcher = KnowledgeBase.ServerBulkImport.withUrlContentFetcher

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