# ToolUp.KnowledgeBase — Technical Guide

Deep-dive companion to `README.md`. Covers the three integration contracts, ingestion runtime interaction, file extraction, reset/dedup semantics, and SDK-shape constraints.

## File layout

KnowledgeBase ships as a three-tier split per the SDK's per-tier
package convention:

```
src/
├── ToolUp.KnowledgeBase/                       legacy .props-only shim directory
│   ├── ToolUp.KnowledgeBase.Server.props       injects ToolUp.KnowledgeBase.Server into consumer's server project
│   ├── ToolUp.KnowledgeBase.Client.props       injects ToolUp.KnowledgeBase.Client sources into consumer's client project
│   ├── README.md                               overview, separation rationale, public surface
│   └── TECHNICAL_GUIDE.md                      this file
├── ToolUp.KnowledgeBase.Core/                  shared types — packaged as a normal .dll
│   └── Shared/
│       ├── SharedTypes.fs                      IngestionStatus, KnowledgeDocument, IngestionStatusNotificationKey
│       └── PlatformKnowledgeApi.fs             Fable.Remoting API contract
├── ToolUp.KnowledgeBase.Server/                server-side surface
│   └── Server/
│       ├── Server.fs                           knowledgeApi, makeIngestionStatusObserver
│       ├── Extractors.fs                       PDF / PPTX / DOCX / XLSX / CSV / TXT extraction
│       ├── IngestionObserver.fs                IIngestionStatusObserver implementation
│       ├── Notes.fs                            note storage + paragraph chunking
│       ├── AIContext.fs / AICompose.fs         Standing AI Context builder + composition
│       ├── IndexStorage.fs / Inventory.fs      per-scope document index
│       ├── PlatformAdmin.fs                    admin operations (ResetIndex etc.)
│       ├── ExtractionErrors.fs / JsonHelpers.fs
│       └── Api/                                handler wiring
└── ToolUp.KnowledgeBase.Client/                Fable client tier
    └── Client/
        ├── ClientModel.fs                      Elmish Model/Msg/init/update; installNarrativeCommit
        ├── ClientView.fs                       Feliz views; KnowledgeBaseView.register()
        ├── PlatformKnowledgeAdminUI.fs         admin UI surface
        ├── Icons.fs                            per-module icon registry
        └── icons/                              icon assets
```

The split mirrors the rest of the SDK: a `.Core` tier holding
Fable-compatible shared types (compiled into both the server `.dll`
and into Fable consumer output via `fable/` source-in-nupkg), a
`.Server` tier holding `net10.0` server-only surface, and a `.Client`
tier holding Fable-compiled client surface.

## The three integration contracts

A deployment that wires KB in (or replaces it via a future `ExternalKnowledgeBase`) must honour these three contracts.

### 1. NarrativeCommit handler

`KnowledgeBaseView.installNarrativeCommit ()` calls `Toolup.NarrativeCommit.install` with a submit handler. Other modules that want to offer a "Save to Knowledge Base" button render `NarrativeRenderer` and let the renderer dispatch through the global hook — no module imports KB types directly. An external KB calls `Toolup.NarrativeCommit.install` with its own handler instead.

The default KB's installation must be gated on the deployment having opted in to the default KB; once `KnowledgeBaseMode` ships in Phase 1e, the gate becomes `mode = DefaultKnowledgeBase | ConfiguredKnowledgeBase _` (an `ExternalKnowledgeBase` doesn't double-register).

### 2. `IIngestionStatusObserver` registration

`KnowledgeBase.Server.makeIngestionStatusObserver` returns an `IIngestionStatusObserver` (interface from `ToolUp.RAG.IngestionTypes`). The composition root passes it into `composeWithRAG`. Server-side wiring stays explicit — the `KnowledgeBaseMode` DU (Phase 1e) only governs *client* auto-injection; the server side remains the deployment's responsibility, mirroring DataManager's `fileManagementApi` wiring.

The observer's `OnChunkIndexed` updates the KB's per-document ingestion status index and publishes `IngestionStatusUpdate` notifications. `OnChunkFailed` records the error and publishes a failure notification. Both are `Async<unit>` and identity is by-value (`IngestionJob.DocumentId : string`), satisfying Phase 9c portability rules.

### 3. Notification-key contract

The wire-format string `"KnowledgeBase.IngestionStatus"` is exposed as `[<Literal>] SharedTypes.IngestionStatusNotificationKey`. The AI assistant's side-panel subscribes to the string literal directly at `src/ToolUp.AI.Client/Client/AIAssistantUI.fs` rather than importing KB's SharedTypes — this preserves the layering (AI is more foundational than KB; AI must not depend on KB). The duplicated literal is therefore a deliberate published contract, not an oversight. An external KB can either publish under the same key (and inherit the stock AI side-panel surface) or define its own (and accept that AI's stock surface won't subscribe to it).

## Ingestion runtime interaction

The two RAG ingestion paths converge on `IngestionBackgroundService`. KB uses **Path A — post-save hook**: `FileManagement.configurePostSaveHooks` installs a hook that runs `VectorisationHandler.Vectorise` after `SessionFileStore.AddFile`. KB registers a `VectorisationHandler` for its own document data type, returning the chunks that should be indexed; the hook then enqueues `IngestionJob`s into the queue.

The flow:

1. User uploads a PDF/PPTX/DOCX/XLSX/CSV via `KnowledgeApi.UploadDocument`.
2. KB extracts text (PdfPig for PDF; DocumentFormat.OpenXml for Office; manual CSV parsing).
3. KB chunks the text and persists a `KnowledgeDocument` with `Status = Queued`.
4. The post-save hook runs the vectorisation handler, which produces chunks and enqueues `IngestionJob` records.
5. `IngestionBackgroundService` dequeues, calls `IRetrievalPipeline.Index`, and invokes the registered `IIngestionStatusObserver` after each chunk.
6. KB's observer transitions the document through `Queued → ExtractingText → Embedding → Complete` (or `Failed`), updates the per-document status index, and publishes `IngestionStatusUpdate` notifications keyed by `IngestionStatusNotificationKey`.

## Notes ingestion path

Notes are user-typed free-form markdown ingested through the same RAG path as uploads — the only difference is the chunker.

1. Client posts `AddNoteRequest = { Title; Body }` to `KnowledgeApi.AddNote`. Empty title or body returns `Error`.
2. Server validates, generates a doc id, persists the body at `knowledge/{docId}/note.md` (mirrors `IngestNarrative`'s `.narrative.md`), and writes a `KnowledgeDocument` with `Source = Note { Title; Author; CreatedAt; LastEditedAt = None }` and `FileType = "note"`.
3. The body is chunked by paragraph: split on `\n\n`, drop empty paragraphs after trim, one chunk per paragraph. For very short bodies (≤ 200 chars after trim), one chunk total. Chunk ids follow `{docId}:chunk:{i}` so per-doc cleanup in `UpdateNote` is precise.
4. Each paragraph chunk is enqueued as an `IngestionJob` via the same queue uploads use. The post-save hook runs the vectorisation handler; the existing `IIngestionStatusObserver` transitions the document through `Queued → Embedding → Complete`.
5. `UpdateNote` re-chunks: scope-deletes prior `{docId}:chunk:{i}` entries from `IVectorStore` (per-doc, not per-scope — `ResetIndex` is the per-scope wipe), bumps `LastEditedAt`, and re-enqueues the new chunks.

Dedup for notes is by `Id` only. Notes have no `(ModuleId, SettingsKey)` natural key the way narratives do — re-saving a note from the editor goes through `UpdateNote` (which has the doc id), not `AddNote`. The narrative-commit dedup branch (`FromNarrative n -> n.ModuleId = … && n.SettingsKey = …`) explicitly returns `false` for `Note _` to keep the path simple.

The notification-key contract (`KnowledgeBase.IngestionStatus`) and the AI-side-panel banner work for notes unchanged — terminal `Complete` / `Failed` transitions publish `IngestionStatusUpdate` like any other document.

## Standing AI Context

Standing AI Context is a single, scope-isolated piece of team-curated markdown the AI assistant sees on **every** message. Distinct from notes:

- **Notes** go through RAG retrieval — only relevant when the user's question touches them.
- **Standing context** goes through prompt injection — always present.

### Storage layout

- Single blob per scope at `knowledge/_ai-context.json`. The leading underscore puts it in a reserved namespace so the dedup logic and the document-list filter can ignore it without special-casing the path.
- Sibling to the per-scope `index.json`; not a `KnowledgeDocument`, not retrievable, not in the vector store.
- Payload is a JSON-serialised `AIContextEntry { Body; UpdatedAt; UpdatedBy }`.

### Builder composition

`KnowledgeBase.Server.standingContextBuilder : IBlobStorage -> ILogger option -> SystemPromptBuilder`

- Conforms to the `SystemPromptBuilder = PromptContext -> Async<string>` alias from `src/ToolUp.AI.Server/Server/SystemPromptBuilder.fs`.
- Returns `""` on:
  - `AccessContext.configScope` returning `None` (Anonymous mode, no persistent scope).
  - The blob not existing (no entry written for this scope yet).
  - `String.IsNullOrWhiteSpace entry.Body` (entry was cleared by the team).
  - Any read or deserialisation exception (logged at warn; the builder is best-effort and must never fault `compose`).
- Otherwise returns the trimmed body. `SystemPromptBuilder.compose` joins all non-empty contributions with `\n\n`; empty contributions are dropped silently.

The deployment composes the builder in `composeWithAI`'s prompt list, **ordered**: platform → active-module → standing-context → page-narrative. Standing context sits between module and narrative because module context is more universal (set once at compose), narrative is most ephemeral (last render only); standing context is the team's persistent layer between the two.

### Per-outer-turn read semantic

The builder runs per outer turn (per `SubmitMessage`), not per inner step of the agent loop. A Save in the AI Context page is reflected on the user's *next* message — no in-flight invalidation needed. This matches `SystemPromptBuilder` semantics across the AI companion.

The cost is one `IBlobStorage.Exists` + one `Download` per outer turn, against a small (typically <100KB) blob. Not currently cached; deployments that want a memory-cache layer can wrap `IBlobStorage` rather than the builder.

### Why not RAG ingestion for standing context?

The whole point of "standing context" is that it's *always* seen, not retrieved when relevant. Routing it through RAG would either require special-casing the chunker to skip it (defeats unity) or get it both retrieved AND prompt-injected (double-counting → biased prompt budget). A separate blob + dedicated builder keeps the two roles clean: RAG for retrieval, builder for injection.

### Why not `IConfigStore`?

`IConfigStore` is built around typed key-value fields with schemas (`Bool`/`Int`/`Float`/`String`/`Choice`). It can technically hold an arbitrarily long `String` field, but the registered-schema admin UI in `TeamConfigUI` is wrong-shaped for a single multi-KB markdown blob with custom rendering. Standalone storage + a bespoke editor is cleaner and keeps `IConfigStore` for what it's good at — typed module configuration.

### RBAC and audit

- `SetAIContext` is gated by `TeamRoles.canWriteTeamConfig` in `Team` / `MultiTeam` modes — Owner/Admin only. Members get a `403`.
- Unrestricted in `Individual` / `AuthenticatedEphemeral` (the user owns their scope).
- Always rejected in `Anonymous` (no persistent scope; `GetAIContext` returns `None`).
- Every successful write emits an `AIContextUpdated` event to `IEventStore`. The audit payload is length-only (`{ UpdatedBy; BodyLength; Cleared }`) — the body lives in the blob; the audit log records change, not contents.

## Reset and dedup semantics

`KnowledgeApi.ResetIndex` is the admin operation that wipes all KB documents in the active scope:

- Deletes the per-document JSON metadata under `team-{id}/knowledgebase/`.
- Deletes the source-document blobs from the storage container.
- Calls `IVectorStore.DeleteByScope` to drop the vectors.
- Clears in-memory caches.
- Publishes `DataRefreshed("KnowledgeBase", scopeId)` so other tabs reload.

The blob deletion is container-locked (a single `ResetIndex` call serialises against concurrent uploads in the same container); this prevents partial-state errors when an upload races with a reset.

Dedup is by content hash on upload — re-uploading the same bytes returns the existing document rather than creating a duplicate. Narrative-commit uses a separate identity (a stable hash of the AI-generated narrative + originating module) so that the same narrative committed twice updates the existing entry instead of creating a new one.

## File extraction notes

All extractors route through `ToolUp.RAG.Chunking` (Phase 14g) for token-aware splitting. Prose content goes through `splitByTokens ChunkingConfig.defaults` (512 tokens, 64 overlap, sentence-aware with word-boundary fallback); spreadsheet content goes through `chunkSpreadsheet ChunkingConfig.tabular` (512 tokens, 0 overlap, 1 min — preserves row identity). The token counter is the heuristic ≈4-chars-per-token default; a `Microsoft.ML.Tokenizers` companion can plug in a real BPE counter via `ITokenCounter`.

- **PDF**: PdfPig — text extraction only; no OCR. Each page goes through `splitByTokens`; pages within budget produce a single `Page N` chunk, longer pages emit `Page N (part X of Y)` sub-chunks. Image-only PDFs produce empty text and are surfaced as `Failed` with a "no text extractable" reason.
- **PPTX**: DocumentFormat.OpenXml — text from slides; each slide goes through `splitByTokens` while preserving its title and `Slide(slideNum, title)` location. Long slides emit `(part X of Y)` sub-chunks under the same slide reference.
- **DOCX**: DocumentFormat.OpenXml — section-aware extraction; each section's body goes through `splitByTokens`; preserves section heading in `Section heading` location with `(part X of Y)` suffix when split.
- **XLSX**: DocumentFormat.OpenXml — schema-+-sample chunk first (`Sheet "X", schema + sample` at `Location = Sheet(sheetName, None)`), then `chunkSpreadsheet` packs token-aware row groups. Each row group emits `Sheet "X", rows N–M of T` with column headers repeated; the `parseRowRange` helper round-trips the row range back into `Location = Sheet(sheetName, Some "rows N–M")` for click-through citations.
- **CSV**: schema-+-sample chunk + `chunkSpreadsheet` row groups (same shape as XLSX), with `Location = RowGroup(startRow, endRow)`. Header is row 1; first data row is row 2.
- **TXT**: token-aware via `splitByTokens` (replaces fixed 50-line chunking); each chunk gets `Location = Section "part X of Y"`.

**Why structure preservation matters:** flattening a spreadsheet to `"Col: val | Col: val"` soup with no row range made tabular queries (`"what was the SKU revenue in row 47"`, `"show me the Q3 figures from the Sales sheet"`) systematically miss the chunks they should have matched. Repeating column headers per chunk + emitting a citable row range gives both BM25 and dense retrieval the schema context they need to score correctly, and lets the AI cite back to a specific row range rather than a vague "somewhere in the sheet".

Extraction is synchronous on the upload thread; chunking happens in-memory; the resulting chunks are handed to the ingestion queue and embedding/indexing happens asynchronously via the background service. Large documents (>100MB) are not currently rejected at the SDK boundary — apps that need a hard limit should layer that in their `IFileProcessor` configuration.

## Portability audit

KB types and interfaces follow Phase 9c rules:

- **Identity by value** — `KnowledgeDocument.Id` is `string`; `IngestionJob.DocumentId` is `string`; observer interface methods take values, not handles.
- **Async at every boundary** — `KnowledgeApi` methods are `Async<_>`; `IIngestionStatusObserver` methods are `Async<unit>`.
- **Stateless handlers** — observer and vectorisation-handler implementations receive all needed context via parameters; KB caches are an in-process performance optimisation, not a correctness requirement.
- **No cross-shard ordering** — ingestion ordering is per-document, never cross-document or cross-team.

## Known limitations

- No incremental re-indexing — `ResetIndex` is the only way to recover from a corrupted index. Re-uploading documents after reset is the documented workflow.
- No per-document retention policy — documents persist until manually deleted or `ResetIndex` is called.
- No per-document permission model — KB documents are scoped at the team level only; finer-grained ACLs are a deployment-layer concern.
- No image-content extraction (no OCR; no chart parsing). Add an OCR provider companion (Phase TBD) if needed.
