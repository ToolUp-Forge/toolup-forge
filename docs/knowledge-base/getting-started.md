# Getting started with ToolUp.KnowledgeBase

End-to-end walkthrough: enable KB in your app, upload a document, watch the assistant ground its answers.

## Prerequisites

- A working ToolUp Platform app with both `ToolUp.AI` and `ToolUp.RAG` enabled — see [AI getting-started](../ai/getting-started.md) and [RAG getting-started](../rag/getting-started.md).

KB is the canonical RAG consumer; without RAG in place, KB has no place to write the indexed content.

## 1. Add the packages

In your server project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.KnowledgeBase.Server" />
</ItemGroup>
```

In your client project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.KnowledgeBase.Client" />
</ItemGroup>
```

`ToolUp.KnowledgeBase.Core` is pulled in transitively.

## 2. Wire the KB module + ingestion observer

In your server composition root:

```fsharp skip=fragment
open ToolUp.KnowledgeBase

let kbModule =
    ServerModule.create "KnowledgeBase"
    |> ServerModule.withGuardedApi KnowledgeBase.Server.knowledgeApi

let ingestionStatusObserver =
    KnowledgeBase.Server.makeIngestionStatusObserver blobStorage (Some notificationChannel) logger

RAGServerApp.create aiProviderFactory providerProfile embedder
|> RAGServerApp.withConfig serverConfig
|> RAGServerApp.withAuth authProvider
|> RAGServerApp.withStorage blobStorage
|> RAGServerApp.addModules [ kbModule ]
|> RAGServerApp.withIngestionObserver ingestionStatusObserver
|> RAGServerApp.run
```

The companion owns its own extraction and chunking path, so there is no KB `DataType` or `VectorisationHandler` to register — `withVectorisation` on a `ServerModule` is for a module contributing its OWN retrievable data. The observer surfaces per-document status to the UI via SSE.

## 3. Wire the client wrapper + narrative-commit

```fsharp skip=fragment
open ToolUp.KnowledgeBase          // KnowledgeBaseMode, KnowledgeBaseConfig
open ToolUp.KnowledgeBase.Client   // KnowledgeBaseClientConfig

let modules = [
    // ... your other modules
]

// Injects the KB module (+ the Platform-Admin content admin) and wires
// the narrative-commit broker, so other modules' "Save to Knowledge
// Base" buttons resolve. See extending.md for the other three modes.
let clientConfig, modules =
    KnowledgeBaseClientConfig.withKnowledgeBase DefaultKnowledgeBase clientConfig modules

AIClientConfig.program aiMode clientConfig modules
|> Program.withReactSynchronous "elmish-app"
|> Program.run
```

## 4. Optional — wire the standing AI context builder

The KB module supports a "Standing AI Context" page where the team can write persistent instructions for the assistant (e.g. "The team focuses on Q3 marketing analysis. Brand names are case-sensitive."). Wire the builder into the AI compose:

```fsharp skip=fragment
let standingContextBuilder =
    KnowledgeBase.Server.standingContextBuilder blobStorage (Some logger)

RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
|> RAGServerApp.withAIConfig {
    AIAssistantServerConfig.defaults with
        SystemPrompt = Some (SystemPromptBuilder.compose [
            SystemPromptBuilder.fromStatic "You are a helpful assistant. ..."
            standingContextBuilder
            // ... other builders (active module, RAG retrieval, etc.)
        ])
}
|> RAGServerApp.run
```

The builder reads the team's standing context per outer turn (cheap blob read) and prepends it to the system prompt. KB never auto-injects — the deployment's composition root is the only place that sees both AI and KB.

## 5. Verify the wiring

Start the server. Visit the app; sign in. The "Knowledge Base" sidebar entry should appear with three pages:
- **Documents** — drag-drop upload area, list of uploaded documents with per-document status.
- **Notes** — text input area, list of saved notes.
- **AI Context** — team-level standing context for the assistant.

## 6. Upload a document

Drag a PDF onto the Documents page. Watch the status panel update through:
- `Pending` — queued
- `Extracting` — multi-format extractor running
- `Chunking` — text split into indexed chunks
- `Embedding` — embedding provider called per chunk
- `Indexed` — chunks visible to retrieval

Status updates flow over SSE; the UI shows live progress. Failed jobs show `Failed` with a one-line reason.

## 7. Chat with the assistant

Open the AI side panel. Ask a question about the document. The retrieval pipeline retrieves matching chunks from the team scope, injects them into the system prompt, and the assistant answers grounded in the retrieved content.

If the assistant doesn't use the retrieved content, check:
- **`MinScore` is too high** — try `withMinScore 0.2`.
- **The document didn't extract well** — check the Documents page status; failed extractions show `Failed`.
- **The embedding model is mismatched** — `/health/rag` shows the active embedder's `ProviderId` / `ModelId`. If you changed it post-ingestion, you'll need to re-embed (see RAG concepts).

## 8. Save from another module — narrative-commit

When another module has content worth indexing (analysis output, generated text, etc.), add a "Save to Knowledge Base" button:

```fsharp skip=fragment
Html.button [
    prop.text "Save to Knowledge Base"
    prop.onClick (fun _ ->
        match Toolup.NarrativeCommit.current () with
        | Some handler -> handler.Submit analysisDocument false |> Async.StartImmediate
        | None -> ())   // no KB composed in this deployment
]
```

The broker takes a `NarrativeDocument` plus an overwrite flag — `false` first, then `true` after the UI has confirmed an overwrite — not a title/body/module triple. `Toolup.NarrativeCommit.current ()` returns `None` when nothing registered a handler, so a module's Save button degrades rather than failing. The handler (`KnowledgeBaseView.narrativeCommitHandler`, wired onto `ClientConfig.Handlers` by `withKnowledgeBase`) persists the narrative and indexes its chunks; it appears in the Documents list with `KnowledgeSource.FromNarrative`.

## 9. Notes

Quick text snippets that don't justify a full document. Type into the Notes page input, hit Enter. The note is persisted, indexed, and visible alongside documents in retrieval. Useful for capturing one-off observations the assistant should know about ("we measure sales in units, not value").

## 10. AI Context (standing instructions)

Visit the AI Context page. Add entries like:
- "Brand names are case-sensitive."
- "The current quarter is Q3 2026."
- "Default analysis window is 4 weeks."

When the standing context builder is wired (Step 4), every chat turn includes these entries in the system prompt. Useful for team-level conventions the assistant should always know.

## Next steps

- [concepts.md](concepts.md) — upload → extract → chunk → ingest pipeline, ingestion-status flow, narrative-commit mechanism.
- [api-reference.md](api-reference.md) — the full `KnowledgeApi` contract + handler shape.
- [extending.md](extending.md) — replacing the built-in module, adding new extractors.
