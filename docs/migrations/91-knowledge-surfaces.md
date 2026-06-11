# Migration — Phase 91: RAG/KB content surfaces + AI authoring hardening

**Status:** additive, opt-in. Zero cost when not composed (GP 13). All four Phase 91 surfaces are now shipped: **RAG answer pages** (`RagAnswerSource`), **KB-as-docs** (`KnowledgeDocsSource`), the **semantic-search SSR endpoint** (`/search?q=`), the **suggested-questions FAQ** (`SuggestedQuestionsSource`), and the **AI authoring hardening** (`NarrativePublishGuardrails`). See [`docs/platform/knowledge-portal.md`](../platform/knowledge-portal.md) for the end-to-end portal narrative.

## What changes

`ToolUp.PublicRendering` gains `RagAnswerSource` — an `IContentSource` ([Phase 83](83-icontentsource.md)) that turns the retrieval pipeline into **public/gated SSR answer pages**. A slug under a configured prefix (e.g. `/answers/how-do-i-deploy`) is resolved at request time via `IRetrievalPipeline.Retrieve` and rendered as a **cited `Narrative`**.

| Symbol | Where | Purpose |
|---|---|---|
| `RagAnswerSource.create` | `Server/RagAnswerSource.fs` | Build the content source over an `IRetrievalPipeline` |
| `RagAnswerConfig` (+ `create` / `withTopK` / `withMinScore` / `strictlyGrounded` / `withSynthesis`) | `Server/RagAnswerSource.fs` | Compose-time config |
| `RagSynthesisHook` | `Server/RagAnswerSource.fs` | Optional LLM-synthesis hook |
| `RagAnswerSource.queryFromSlug` | `Server/RagAnswerSource.fs` | Slug-tail → query helper |
| `KnowledgeDocsSource.create` + `IKnowledgeDocsProvider` + `KnowledgeDocsConfig` | `Server/KnowledgeDocsSource.fs` | KB-as-docs: collection index + per-doc landing, enumerable for sitemap/export |
| `SemanticSearchHandler.handle` + `SemanticSearchConfig` + `SemanticSearch.*` | `Server/SemanticSearchHandler.fs` | `/search?q=` SSR endpoint (query-size cap, deep-link builder) |
| `PublicRenderingServerApp.withSemanticSearch` | `Server/PublicRenderingCompose.fs` | Mount `/search` (pipeline resolved from DI) |
| `SuggestedQuestionsSource.create` + `SuggestedQuestionsProvider` + `SuggestedQuestionsConfig` | `Server/RagAnswerSource.fs` | FAQ index seeding the answer pages |
| `NarrativePublishGuardrails` (+ `defaults` / `aiHardened` / `with*`) | `Server/NarrativePagePublisher.fs` | AI-publish guardrails: forced-Draft, layout allow-list, forced audience, section cap |
| `PublicRenderingServerApp.withAIPublishGuardrails` | `Server/PublicRenderingCompose.fs` | Wire the guardrails (consulted only when `AIPublishEnabled = true`) |

## Grounding — the load-bearing safety property

- **Extractive by default (grounded by construction).** The answer is the retrieved chunks rendered as cited blockquotes plus the top chunk as the lead — every word shown came from a retrieved source, so there is no hallucination surface and **no LLM dependency** (GP 13).
- **Optional synthesis.** `withSynthesis` wires the deployment's own `IAIProvider`-backed prose generator. The SDK never calls an LLM itself.
- **`StrictlyGrounded`.** `strictlyGrounded` makes a query with no match at/above `MinScore` render a graceful **no-answer page** (a Warning callout stating the KB has nothing relevant) — never speculative prose, **even when a synthesis hook is present**. The contract is modelled locally (not by referencing `ToolUp.RAG.Server.GroundingMode`) so PublicRendering's dependency graph stays free of the RAG companion (GP 1).
- **Scope isolation (GP 4).** Retrieval runs with the request's resolved `AccessContext`, so the pipeline's own team-scope validation applies — a caller only retrieves from scopes it is authorised for. Combine with [Phase 86](86-gated-ssr.md) `audience:` gating for internal-only knowledge portals.

## Adopting it

```fsharp
open ToolUp.PublicRendering

// `pipeline` is the deployment's IRetrievalPipeline (from ToolUp.RAG compose).
let answers =
    RagAnswerSource.create pipeline (
        RagAnswerConfig.create "answers" [ VectorScope.Team teamId ]
        |> RagAnswerConfig.withTopK 6
        |> RagAnswerConfig.withMinScore 0.35
        |> RagAnswerConfig.strictlyGrounded)   // refuse rather than speculate

ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withContentSource answers
    // answer pages are expensive — cache them (Phase 84):
    |> PublicRenderingServerApp.withRenderCache (InMemoryRenderCache.create ())
    |> PublicRenderingServerApp.withRenderCacheDefaultPolicy (Cache(300, true)))
|> ServerApp.run
```

## Breaking change

None. `RagAnswerSource` is opt-in; a deployment that never composes it is unaffected.

## The other surfaces (one-liners)

- **KB-as-docs** — `KnowledgeDocsSource.create provider config` serves a collection index (`/<prefix>`) and per-document landings (`/<prefix>/<id>`) from a deployment-supplied `IKnowledgeDocsProvider`. It also implements `IEnumerableContentSource`, so the doc pages appear in `sitemap.xml` + static export. The provider keeps PublicRendering free of a KB dependency (GP 1); the KB's `SourceLocation` hint becomes a section deep-link anchor.
- **Semantic search** — `withSemanticSearch config` mounts `/search?q=`, runs `IRetrievalPipeline.Retrieve` (resolved from DI) under the request scope, and renders a ranked, indexable result list. The query-size cap (`MaxQueryChars`, default 16384) refuses over-long queries with a `400`; the route otherwise rides the deployment's general rate-limit partition (Phase 14y posture). Results deep-link into KB-as-docs via an opt-in `withResultLink` (e.g. `SemanticSearch.docsLinkByChunkId "docs"`).
- **Suggested-questions FAQ** — `SuggestedQuestionsSource.create provider config` renders an FAQ index linking each question into `/<answersPrefix>/<slug>`; `questionSlug` is the inverse of `RagAnswerSource.queryFromSlug`, so a click lands on the grounded answer page.
- **AI authoring hardening** — `withAIPublishGuardrails NarrativePublishGuardrails.aiHardened` forces every `publish_narrative` page to land as a **Draft** (Phase 89 review workflow) instead of going live; `withForceDraft` / `withAllowedLayouts` / `withAudience` / `withMaxSections` tune the constraints. Defaults reproduce the Phase 80a immediate-publish behaviour (GP 11).

## Verification

- `dotnet run --project Build.fsproj -- VerifyAll` — the `KnowledgeSurface (Phase 91)` suite (26 tests) covers: `RagAnswerSource` (`queryFromSlug`, extractive cited answer, **StrictlyGrounded refusal** incl. refuse-despite-synthesis-hook + MinScore-drops-weak, synthesis-hook prose, fall-through); `KnowledgeDocsSource` (index links, per-section location-hint anchors, unknown-id fall-through, route enumeration, empty-state); semantic search (query classification incl. query-size cap, ranked result list, no-results callout, deep-link builder); the AI guardrails (default preserves Published/Public, forced-draft → not publicly visible, audience pin, layout-allow-list refusal, section-cap refusal); and the FAQ surface (slug round-trip, links into the answers prefix, prefix isolation, empty-state).

## See also

- [`docs/migrations/83-icontentsource.md`](83-icontentsource.md) — the content-source seam.
- [`docs/migrations/84-ssr-render-cache.md`](84-ssr-render-cache.md) — caching expensive answer pages.
- [`docs/platform/gated-ssr.md`](../platform/gated-ssr.md) — gating a knowledge portal to internal audiences.
