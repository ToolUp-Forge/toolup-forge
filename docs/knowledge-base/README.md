# ToolUp.KnowledgeBase

User-facing canonical consumer of `ToolUp.RAG`. Multi-page Feliz module covering document upload + multi-format extraction (PDF / PPTX / DOCX / XLSX / CSV / TXT / MD), notes, AI context, narrative-commit, ingestion-status surfacing. Drop it into an app that uses RAG and users have a working knowledge base.

Layering:

```
ToolUp.Platform       ← interfaces, shell, file management, scope, RBAC
   ↑
ToolUp.AI             ← agent loop, system-prompt composition, BYOK
   ↑
ToolUp.RAG            ← vector store, retrieval pipeline, ingestion runtime
   ↑
ToolUp.KnowledgeBase  ← document upload, extraction, KB UI, narrative-commit
```

RAG knows nothing about KB; KB depends on RAG (registers an `IIngestionStatusObserver`, enqueues `IngestionJob`s).

## When to use this companion

- Users need to upload documents (PDFs, reports, spreadsheets) and chat with an assistant grounded in them.
- "Save to Knowledge Base" buttons on other modules (narrative-commit).
- Per-team isolated knowledge bases — each team sees only their own documents.
- Notes — short text snippets the user types directly, indexed alongside files.
- Standing AI context — persistent team-level instructions the assistant always sees.

## When NOT to use this companion

- **Programmatic-only ingestion** — depend on `ToolUp.RAG.Core`'s `IngestionQueue` directly; skip the KB UI.
- **Custom document upload flows** — keep RAG, write your own module against the `IRetrievalPipeline` interface and `VectorisationHandler` extension point.
- **Single-format ingestion** (only PDFs, only CSVs) — the multi-format extraction layer carries weight you don't need; write a thinner consumer.

## What's in the box

Three packages:

| Package | What it is |
|---|---|
| `ToolUp.KnowledgeBase.Core` | Shared types: `KnowledgeApi` ToolUp.Remoting contract, `KnowledgeDocument`, `IngestionStatus`, `KnowledgeSource` (`UploadedFile \| FromNarrative \| Note`), `NoteSource`, `AddNoteRequest`, `UpdateNoteRequest`, `AIContextEntry`, `IngestionStatusUpdate`. Plus the wire-format literal `IngestionStatusNotificationKey = "KnowledgeBase.IngestionStatus"`. |
| `ToolUp.KnowledgeBase.Server` | Document upload + multi-format extraction (PDF / PPTX / DOCX / XLSX / CSV), ingestion observer, narrative-commit, notes / AI-context API, `KnowledgeBase.Server.knowledgeApi`. Depends on `ToolUp.AI.Server` + `ToolUp.RAG.Server`. Heavy NuGet deps (`PdfPig`, `DocumentFormat.OpenXml`) stay scoped here. |
| `ToolUp.KnowledgeBase.Client` | Multi-page Feliz module: `/documents`, `/notes`, `/platform-library`, `/ai-context`. Narrative-commit broker (`KnowledgeBaseView.narrativeCommitHandler`), the `KnowledgeBaseMode` override DU + `KnowledgeBaseClientConfig.withKnowledgeBase`. KB icons. |

## Quick start

Add the packages:

```xml
<PackageReference Include="ToolUp.KnowledgeBase.Server" />
<PackageReference Include="ToolUp.KnowledgeBase.Client" />
```

Wire the server composition root (depends on `RAGServerApp` being in place):

```fsharp
open ToolUp.KnowledgeBase

// In the module registration list:
let kbModule =
    ServerModule.create "KnowledgeBase"
    |> ServerModule.withGuardedApi KnowledgeBase.Server.knowledgeApi
    |> ServerModule.withDataTypes [ KnowledgeBase.Server.kbDataType ]

let ingestionStatusObserver = KnowledgeBase.Server.makeIngestionStatusObserver()

RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> RAGServerApp.withConfig serverConfig
|> RAGServerApp.addModules [ kbModule; (* other modules *) ]
|> RAGServerApp.withIngestionStatusObserver ingestionStatusObserver
|> RAGServerApp.run
```

Wire the client composition root:

```fsharp
open ToolUp.KnowledgeBase          // KnowledgeBaseMode, KnowledgeBaseConfig
open ToolUp.KnowledgeBase.Client   // KnowledgeBaseClientConfig

let modules = [
    // ... other modules
]

let clientConfig, modules =
    KnowledgeBaseClientConfig.withKnowledgeBase DefaultKnowledgeBase clientConfig modules

AIClientConfig.program aiMode clientConfig modules
|> Program.withReactSynchronous "elmish-app"
|> Program.run
```

That's it. The Knowledge Base sidebar entry appears with three pages: Documents (upload + list + delete), Notes (text snippets), AI Context (standing team instructions for the assistant).

## Three integration contracts

External KB replacements (apps that want to provide their own KB module instead of using the built-in one) must honour:

1. **NarrativeCommit handler** — call `Toolup.NarrativeCommit.install` with your submit handler. Other modules' "Save to Knowledge Base" buttons resolve through this hook.
2. **`IIngestionStatusObserver`** — wired explicitly into `composeWithRAG`; not auto-injected. The observer surfaces per-document status (Pending / Extracting / Chunking / Embedding / Indexed / Failed) to the UI.
3. **Notification-key contract** — the literal `"KnowledgeBase.IngestionStatus"` is the wire format the AI side panel subscribes to. An external KB either matches it or accepts that the AI panel won't surface its progress.

These are the only contracts. Internally, `KnowledgeBase.Server` is just an `IFormApi`-like ToolUp.Remoting handler that the SDK shell injects.

## Multi-format extraction

The shipped extractors:
- **PDF** — `UglyToad.PdfPig`. Per-page text extraction. Scanned PDFs (image-only) get empty text; pair with an `IOcrProvider` companion (deferred) to fill them.
- **PPTX** — `DocumentFormat.OpenXml`. Per-slide text + speaker notes.
- **DOCX** — `DocumentFormat.OpenXml`. Paragraph-by-paragraph; tables flattened to row-comma-separated.
- **XLSX** — `DocumentFormat.OpenXml`. Per-sheet, header-aware chunking.
- **CSV / TSV** — header-aware row-wise chunking.
- **TXT / MD** — passed through to `Chunking.splitByTokens`.

Other formats are accepted as raw text; deployments needing them write a custom `VectorisationHandler` per format.

## Phase-coherent terminology

In this companion's docs, "chunk" means the unit of indexed text (post-splitting); "document" means the user-uploaded file. A 100-page PDF produces one document and ~50-500 chunks depending on chunking config.

## Concepts

See [concepts.md](concepts.md) for the upload → extract → chunk → ingest pipeline, ingestion-status flow, narrative-commit mechanism, notes + AI context storage model.

## API reference

See [api-reference.md](api-reference.md) for the full `KnowledgeApi` ToolUp.Remoting contract, server-side handler shape, client view types.

## Extending

See [extending.md](extending.md) for replacing the built-in KB module with a custom one, adding new extractors, customising the upload-status UI.
