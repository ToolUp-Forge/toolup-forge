# Concepts

How `ToolUp.KnowledgeBase` works under the hood. The companion is a thin glue layer between `ToolUp.RAG`'s ingestion pipeline and the user-facing module surface.

## Pipeline overview

```
user drops file into Documents page UI
   │
   ▼  POST /api/IKnowledgeApi/UploadDocument
   ▼  KnowledgeBase.Server.knowledgeApi handler
   ▼  IBlobStorage.Save → team-{teamId}/kb-documents/{docId}.{ext}
   │
   ▼  (post-save hook fires)
   ▼  KnowledgeBase.Server.kbVectorisationHandler.Vectorise
   │       │
   │       ▼  multi-format extraction (PdfPig / OpenXml / ...)
   │       ▼  Chunking.splitByTokens (token-aware)
   │       ▼  returns TextChunk list
   │
   ▼  IngestionQueue.Enqueue { DocumentId; Scope; Chunks }
   │
   ▼  IngestionBackgroundService dequeues
   │       │
   │       ▼  IEmbeddingProvider.GenerateEmbedding per chunk
   │       ▼  IVectorStore.Index per chunk
   │       ▼  emits KnowledgeChunkIndexed event
   │       │
   │       ▼  IIngestionStatusObserver.OnChunkIndexed
   │       ▼  Notification.Publish (NotificationKind = "KnowledgeBase.IngestionStatus")
   │
   ▼  SSE event reaches client; KB Documents page updates per-document status pill
```

End-to-end latency for a typical 10-page PDF: ~5-15 seconds.

## Document upload + extraction

`KnowledgeBase.Server.knowledgeApi.UploadDocument` accepts a `byte[]` + filename + content-type. The handler:

1. Validates the file (size cap, content-type allowlist).
2. Generates a `DocumentId: Guid`.
3. Writes to `IBlobStorage`: container `team-{teamId}`, key `kb-documents/{documentId}.{ext}`.
4. Writes the document metadata blob: `kb-documents/{documentId}.meta.json` — `{ DocumentId; FileName; ContentType; UploadedBy; UploadedAt; Source = UploadedFile; Status = Pending }`.
5. Emits `DocumentUploaded` event under `_platform.kb`.

The post-save hook in `IDataObjectStore` (when registered with `composeWithRAG`) triggers `kbVectorisationHandler.Vectorise`. The handler:

1. Reads the document bytes from `IBlobStorage`.
2. Routes by content-type to the matching extractor.
3. Runs the extractor to get raw text per page / per sheet / per slide.
4. Calls `Chunking.splitByTokens` with the configured `ChunkingConfig`.
5. Returns `TextChunk list` to the ingestion queue.

If extraction fails (corrupted PDF, password-protected DOCX, etc.), the handler emits a `DocumentExtractionFailed` event with the reason; the document's status becomes `Failed`. The user sees a one-line error in the Documents page.

### Upload policy (`withUploadPolicy`)

By default the upload boundary imposes no size cap and no type allowlist, and stores a type it has no extractor for as `UnsupportedFormat` ("stored, not searchable") rather than the misleading `Complete 0`. Compose `KnowledgeBase.Server.withUploadPolicy` to tighten this:

```fsharp skip=fragment
app
|> KnowledgeBase.Server.withUploadPolicy {
    KnowledgeUploadPolicy.permissive with
        MaxUploadBytes = Some (25L * 1024L * 1024L)        // 25 MB hard cap
        AllowedExtensions = Some (Set.ofList [ "pdf"; "docx"; "csv" ])
        OnUnsupportedType = Reject                          // refuse, don't store-unindexed
}
```

- **`MaxUploadBytes`** — enforced at the KB boundary *before* the bytes are persisted; an oversize upload is refused with `UploadRejected` and nothing is stored. (`None` leaves only Kestrel's `MaxRequestBodySize` as a lever, which still buffers the whole `byte[]` in memory.)
- **`AllowedExtensions`** — `None` allows any extension; a `Some` set rejects anything outside it.
- **`OnUnsupportedType`** — `Reject` refuses an unrecognised type outright; `AcceptUnindexed` (the default) stores the original so it stays downloadable but flags it `UnsupportedFormat`.
- **`AcceptUnboundedUploads`** — explicit opt-out for the `Team` / `MultiTeam` preflight `Warning` that fires when `MaxUploadBytes = None`.

**Filename sanitisation is always on**, independent of policy: the client-supplied filename is passed through `Path.GetFileName` and validated, so `../../index.json` is stored under a server-controlled `knowledge/{docId}/index.json` key and can never reach the container-root `knowledge/index.json`.

### Serving originals safely

If you wire an HTTP endpoint that serves `OriginalDocument.Content` (a download / preview link), **set `Content-Disposition: attachment`** for inline-renderable types and pin the `Content-Type` from `OriginalDocument.ContentType` rather than letting the browser sniff. KB accepts arbitrary user content; csv / md / html / svg originals carry active markup and, served inline, execute in the deployment's origin — a stored-XSS vector.

## Multi-format extractors

Shipped extractors:

| Format | Library | Output |
|---|---|---|
| PDF | `UglyToad.PdfPig` | Per-page text, page-numbered. Scanned PDFs have no text layer → `IngestionStatus.OcrUnavailable` unless an `IOcrProvider` companion is composed (`ToolUp.OcrProviders.Tesseract`). |
| Images (`png` `jpg` `tiff` `bmp` `gif` `webp`) | `IOcrProvider` companion | One OCR page. Without a companion: stored, `OcrUnavailable`. Not in `supportedExtensions`, so the upload policy's allowlist / reject behaviour is unchanged. |
| PPTX | `DocumentFormat.OpenXml` | Per-slide text + speaker notes; preserves slide order. |
| DOCX | `DocumentFormat.OpenXml` | Paragraph-by-paragraph; tables flattened to comma-separated rows. |
| XLSX | `DocumentFormat.OpenXml` | Per-sheet; passed to `Chunking.chunkSpreadsheet` for header-aware chunking. |
| CSV / TSV | Built-in | Header-aware row-wise chunking. Auto-detects delimiter. |
| TXT | Built-in | Passed directly to `Chunking.splitByTokens`. |
| MD | Built-in | Same as TXT; markdown structure isn't preserved as metadata (chunks are plain text). |

Extraction is best-effort. The extractor's job is to produce text; if a format ships malformed (truncated PDF, broken zip in PPTX), the extractor emits what it could parse and the handler logs a warning. No-text result is the failure mode — the document is `Failed` and not re-tried automatically.

### Adding a new extractor

A custom extractor lives in your own module's `VectorisationHandler` for now. The shipped KB extractor list isn't extensible from outside the package — replace the KB module entirely (see [extending.md](extending.md)) if you need to add formats.

A future extension point (`IDocumentExtractor` interface registered by content-type) is a planned addition.

## Ingestion-status surfacing

`IIngestionStatusObserver` is the contract:

```fsharp skip=signature
type IIngestionStatusObserver =
    abstract OnChunkIndexed: IngestionJob -> Async<unit>
    abstract OnChunkFailed: IngestionJob * error: string -> Async<unit>
```

The job itself carries the identity the observer needs, so there are no separate job-id / chunk-id parameters and no accepted / completed callbacks — completion is derived from the indexed count reaching the job's total.

`KnowledgeBase.Server.makeIngestionStatusObserver storage notificationChannel logger` wires an implementation that:
1. Resolves the document from the `IngestionJob`.
2. Advances its persisted status through `Queued → ExtractingText → Embedding (n, total) → Complete n | Failed reason`.
3. On a terminal status, publishes a `CustomNotification` under `KnowledgeBase.IngestionStatus` to the scope — after re-checking the job's `OriginatingUserId` against the stored `UploadedBy`, so a spoofed enqueue cannot deliver another user's status into the wrong scope.

The SSE wire format for the notification is `IngestionStatusUpdate`. It is deliberately DU-free, so a subscriber in another companion can parse it without mirroring `IngestionStatus` across the module boundary — and only terminal outcomes are published:

```fsharp
type IngestionStatusUpdate = {
    DocumentId: string
    FileName: string
    Outcome: string       // "Complete" or "Failed"
    ChunkCount: int       // chunk count when Complete, 0 otherwise
    ErrorReason: string   // reason when Failed, "" otherwise
    UploadedBy: string
}
```

The KB Documents page subscribes to this notification key and updates the per-document status pill live. The AI assistant side panel also subscribes — when documents are still indexing, it can show "indexing in progress" before answering.

### Notification-key contract

The literal `"KnowledgeBase.IngestionStatus"` is a published wire-format contract. External KB replacements should match this string exactly — otherwise the AI side panel won't surface ingestion progress.

## Narrative-commit

Other modules can deposit content into KB via narrative-commit:

```fsharp skip=fragment
Html.button [
    prop.text "Save to Knowledge Base"
    prop.onClick (fun _ ->
        match Toolup.NarrativeCommit.current () with
        | Some handler -> handler.Submit analysisDocument false |> Async.StartImmediate
        | None -> ())   // no KB composed in this deployment
]
```

`Toolup.NarrativeCommit` is a registry with no compile-time dependency on KB: `current ()` returns the installed `NarrativeCommitHandler`, whose `Submit` takes a `NarrativeDocument` and an overwrite flag (`false` first; the UI re-submits `true` after confirming). `None` means nothing registered a handler, so the Save button degrades instead of failing. The handler is `KnowledgeBaseView.narrativeCommitHandler`, wired onto `ClientConfig.Handlers.NarrativeCommitHandler` at compose time (directly, or by `KnowledgeBaseClientConfig.withKnowledgeBase`).

The handler:
1. Sends the narrative + title via `KnowledgeApi.IngestNarrative` (ToolUp.Remoting).
2. Server-side: persists a document blob with `Source = FromNarrative { SourceModule; Title }`.
3. Runs through the same vectorisation handler + ingestion pipeline as a file upload.

Narratives appear in the Documents list with a different icon and origin label. Users can delete them like any other document.

This gives modules a clean way to push their content into the KB without coupling to KB internals — the `Toolup.NarrativeCommit` shim is the only contract.

## Notes

Notes are a separate persistence path with the same vector-indexing target. A note is just a single chunk (or a small chunk-set if it's long enough to split). Notes are stored alongside documents (same blob container) but with `Source = Note` and a different metadata shape.

The Notes page UI is a simple text-input → list-of-notes paradigm. No upload, no extraction — the user types, the SDK indexes.

Use case: capturing one-off observations the assistant should know ("we measure sales in units, not value"; "Q3 analysis should exclude store 47"; "client confirmed the launch is week of Oct 10").

## AI Context (standing instructions)

The AI Context page is a different shape — entries are NOT indexed for retrieval. They're injected directly into the system prompt every turn:

```fsharp skip=fragment
let standingContextBuilder = KnowledgeBase.Server.standingContextBuilder blobStorage (Some logger)
```

The builder reads `_platform/kb-ai-context/{teamId}/entries.json` per request (cheap blob read; sub-millisecond), formats the entries as a system-prompt section, returns the string.

Use cases:
- Brand-name conventions ("brand names are case-sensitive").
- Date conventions ("the current quarter is Q3 2026").
- Default settings ("default analysis window is 4 weeks").
- High-level domain context ("the team analyses pharma marketing data").

Limit ~20 entries / ~2000 tokens per team. Beyond that, push into notes (indexed) so retrieval picks them up only when relevant.

The standing-context builder is **opt-in** — deployments wire it explicitly into their `withAIConfig` system-prompt list. KB doesn't auto-inject because AI doesn't depend on KB (one-way layering); the deployment composition root is the only place that sees both.

## Reset / dedup

The KB Documents page has a "Reset KB" button (admin-only). When invoked:

1. Confirmation dialog ("This will delete N documents and Y chunks. Proceed?").
2. `KnowledgeApi.ResetKnowledgeBase` is called.
3. Server-side: deletes all KB document blobs in the scope, calls `IVectorStore.DeleteByScope`, clears the AI context.
4. Emits `KnowledgeBaseReset` event under `_platform.kb`.

`DeleteByScope` is a config-grade reset (bypasses tombstone soft-delete semantics). The chunks are physically gone, not soft-deleted; there's no recovery path.

Dedup: when a file with the same content-hash is uploaded again, the SDK detects it via `IDataObjectStore`'s content-addressable layer and re-uses the existing storage blob. The document entry is new (different `DocumentId`), but the underlying bytes don't duplicate.

Chunk-level dedup is the vector store's responsibility — duplicate chunk text within the same scope is left for the vector store to filter (or not, depending on impl).

## Scope isolation

Every KB document lives in `team-{teamId}/kb-documents/`. The scope filter in `IRetrievalPipeline.Retrieve` ensures Team A's caller cannot retrieve Team B's chunks even if they know the document IDs.

On team switch (in `MultiTeam` mode), the KB module's `Init` re-fires; the Documents / Notes / AI Context pages refetch against the new team scope. Documents from the prior team disappear; documents from the new team appear.

The Reset KB button is scoped to the active team — no path resets across teams.

## Performance

- **Upload throughput**: bounded by extraction speed. PDFs are slowest (~1s per 10 pages with PdfPig). PPTX / DOCX / XLSX faster. CSV / TXT near-instant.
- **Indexing throughput**: bounded by embedding-provider rate limits. Default ingestion concurrency is 2; raise via `withIngestionConcurrency` if your provider supports higher concurrency.
- **Retrieval latency**: usually 50-200ms per query for in-memory vector store + cached embedding. Sparse-only or hybrid adds 30-100ms for BM25 over the same scope.
- **Scaling ceiling**: ~50K chunks per scope before in-memory vector store slows. For larger, switch to `ToolUp.VectorStores.Hnsw` or a future distributed store.

## What KB does NOT cover

- **Versioning of documents** — uploading the same file twice creates two `DocumentId`s. There's no "this is v2 of doc X" semantics. For versioning, use `IDataObjectStore` directly with `VersioningPolicy = Versioned`.
- **Document-level metadata extraction** — extractors extract text, not structured metadata (authors, dates, tags). For metadata-rich knowledge graphs, build a custom module.
- **Multimodal content** — images / audio / video are not extracted. Future multimodal extension via `IImageEmbedder` (deferred).
- **Cross-document linking** — chunks don't know about each other beyond the document boundary. For cross-document reasoning, the assistant uses retrieval + agent loop; the SDK has no native graph layer.

For any of these, the right shape is a custom module on top of `ToolUp.RAG`, replacing KB or running alongside it.
