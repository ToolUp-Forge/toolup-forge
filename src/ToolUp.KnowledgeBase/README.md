# ToolUp.KnowledgeBase

Document upload, extraction, multi-format parsing, ingestion-status surfacing, narrative-commit, and reset/dedup — the canonical user-facing consumer of `ToolUp.RAG`. Ships as a SDK companion (parallel to `ToolUp.AI` and `ToolUp.RAG`); apps that want a knowledge base import the props and reference the companion, apps that don't omit both.

## Why a companion (not a module under `src/Modules/`)

The KnowledgeBase module is sector-agnostic infrastructure: every analytical app on the SDK is a candidate consumer (a media-agency app, a legal-research app, a pharma-analytics app would all want the same upload + extract + index + dedup + narrative-commit surface). It is also the only entry under `src/Modules/` that hard-references `ToolUp.RAG.fsproj` — the companion split already exists in the build graph; promotion to `src/ToolUp.KnowledgeBase/` reflects it in the directory layout and aligns with the eventual NuGet split (Phase 11) and solution split (Phase 13).

## Why ToolUp.RAG and ToolUp.KnowledgeBase stay separate

RAG is general-purpose retrieval-augmentation infrastructure (vector store, retrieval pipeline, ingestion runtime, RAG-aware prompt builder); KB is the canonical user-facing consumer of that infrastructure. They are deliberately not merged because:

- RAG has documented non-KB consumers — module-emitted vectorisation via `VectorisationHandler`, AI conversation memory, third-party sync companions (Confluence, Notion, Slack), headless RAG deployments. Merging would force every such consumer to inherit KB's PDF / PPTX extraction surface and UI.
- Heavy NuGet deps (`PdfPig`, `DocumentFormat.OpenXml`) stay scoped to KB; deployments that want vector search without document upload don't pay the bundle cost.
- The split mirrors the AI / AIAssistant layering: ToolUp.AI is infrastructure, the AI-assistant module is the canonical consumer; ToolUp.RAG is infrastructure, KB is the canonical consumer. Promoting KB clarifies the boundary, it doesn't argue for fusion.

See [`src/ToolUp.RAG/README.md`](../ToolUp.RAG/README.md) for the full RAG surface.

## Layering

```
ToolUp.Platform       ← interfaces, shell, file management, scope, RBAC
   ↑
ToolUp.AI             ← agent loop, system-prompt composition, BYOK
   ↑
ToolUp.RAG            ← vector store, retrieval pipeline, ingestion runtime
   ↑
ToolUp.KnowledgeBase  ← document upload, extraction, KB UI, narrative-commit, reset
```

No cycles. RAG knows nothing about KB; KB depends on RAG (today). KB's server registers an `IIngestionStatusObserver` and enqueues `IngestionJob`s; RAG calls back via the observer interface.

## Public surface

**Server** (`module KnowledgeBase.Server`):
- `knowledgeApi` — ToolUp.Remoting `KnowledgeApi` implementation (upload, list, delete, reset, ingest narrative, **add/update note**, **get/set AI context**).
- `makeIngestionStatusObserver` — factory for the `IIngestionStatusObserver` registered with `composeWithRAG`.
- `standingContextBuilder : IBlobStorage -> ILogger option -> SystemPromptBuilder` — opt-in AI prompt builder that reads the team's standing context per outer turn. Composed by the deployment in `composeWithAI`'s prompt list — KB never auto-injects it (AI doesn't depend on KB; the deployment's composition root is the only place that sees both).

**Client** (`module KnowledgeBase`):
- `KnowledgeBaseView.installNarrativeCommit ()` — installs the global `Toolup.NarrativeCommit` handler so other modules' "Save to Knowledge Base" buttons resolve.
- `KnowledgeBaseView.register ()` — returns the `ErasedModule` for SDK registration. Multi-page module with `/documents`, `/notes`, `/ai-context` pages.

**Shared** (`module SharedTypes`):
- `KnowledgeApi` record (ToolUp.Remoting contract) — adds `AddNote`, `UpdateNote`, `GetAIContext`, `SetAIContext`.
- `KnowledgeDocument`, `IngestionStatus`, `KnowledgeSource` (now `UploadedFile | FromNarrative _ | Note _`), `NoteSource`, `AddNoteRequest`, `UpdateNoteRequest`, `AIContextEntry`, `IngestionStatusUpdate`.
- `[<Literal>] IngestionStatusNotificationKey = "KnowledgeBase.IngestionStatus"` — published wire-format key. The AI-assistant side panel subscribes to this string literal directly so it does not need to depend on KB; the contract is published, not imported.

### Content kinds

The KB stores three content kinds, all in the same vector store and document index:

| Kind | Source | Ingestion path | Use |
|---|---|---|---|
| **Uploaded files** | `KnowledgeApi.UploadDocument` | extract → chunk → enqueue | PDFs, Office docs, CSV |
| **Narrative commits** | `KnowledgeApi.IngestNarrative` (via `Toolup.NarrativeCommit`) | re-chunk on commit | "Save to Knowledge Base" from a module's `NarrativeRenderer` |
| **Notes** | `KnowledgeApi.AddNote` / `UpdateNote` | paragraph-chunk → enqueue | Free-form team prose: decisions, conventions, context |

### Standing AI Context

A separate, single-blob piece of team-curated content the AI assistant sees on **every** message — the equivalent of a `CLAUDE.md` for the team. Stored at `knowledge/_ai-context.json` (one entry per scope) and exposed via `GetAIContext` / `SetAIContext`. Composed into the system prompt by `standingContextBuilder` per outer turn (per `SubmitMessage`). Owner/Admin-gated writes in Team / MultiTeam modes; rejected in Anonymous (no persistent scope).

## How to enable

`src/ToolUpApp-Server/ToolupApp-Server.fsproj`:

```xml
<Import Project="..\ToolUp.KnowledgeBase\ToolUp.KnowledgeBase.Server.props" />
<ProjectReference Include="..\ToolUp.KnowledgeBase\ToolUp.KnowledgeBase.fsproj" />
```

`src/ToolUpApp-Client/ToolupApp-Client.fsproj`:

```xml
<Import Project="..\ToolUp.KnowledgeBase\ToolUp.KnowledgeBase.Client.props" />
```

`src/ToolUpApp-Server/Server.fs` — register the API, the observer, and (opt-in) the standing-context builder:

```fsharp
let kbModule =
    ServerModule.create "KnowledgeBase"
    |> ServerModule.withGuardedApi (KnowledgeBase.Server.knowledgeApi (* deps *))
// pass observer into composeWithRAG
... composeWithRAG (KnowledgeBase.Server.makeIngestionStatusObserver ...) ...

// Compose standing AI context into the AI system prompt. Order matters:
// platform → active-module → standing-context → page-narrative.
let aiAssistantConfig = {
    Branding = { ... }
    SystemPrompt = Some (Prompt.compose [
        Prompt.fromStatic platformPrefix
        Prompt.activeModuleContext
        KnowledgeBase.Server.standingContextBuilder blobStorage (Some logger)
        Prompt.currentNarrativeContext
    ])
}
```

`src/ToolUpApp-Client/Client.fs` — register the module + narrative handler:

```fsharp
KnowledgeBaseView.installNarrativeCommit ()
Client.run config [ ...; KnowledgeBaseView.register (); ... ]
```

To remove the knowledge base from a deployment: strip the two props imports + the project reference + the four lines above. The build is clean without them. Removing `standingContextBuilder` from the prompt-builder list leaves AI behaviour unchanged — the builder is opt-in.

## Phase 1e (deferred): `KnowledgeBaseMode` DU

A four-case override mode (`No` / `Default` / `Configured` / `External`) parallel to `DataManagerMode` is planned for Phase 1e — it lets a deployment substitute a custom KB module (Confluence sync, Notion sync, custom dedup) without removing imports. Phase 1d (this extraction) is purely a directory move; the DU lands in a focused follow-up.

## Companion docs

- `TECHNICAL_GUIDE.md` — three integration contracts (NarrativeCommit handler, `IIngestionStatusObserver`, notification-key contract), reset/dedup semantics, file-extraction notes.
