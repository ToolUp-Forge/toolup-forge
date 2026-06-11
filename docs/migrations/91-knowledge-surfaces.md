# Migration — Phase 91: RAG/KB content surfaces (RagAnswerSource)

**Status:** additive, opt-in. Zero cost when not composed (GP 13). This migration covers the **RAG answer-page surface** shipped first; the remaining Phase 91 surfaces (KB-as-docs, semantic-search endpoint, `publish_narrative` draft-gating) are tracked as follow-ons in the roadmap phase.

## What changes

`ToolUp.PublicRendering` gains `RagAnswerSource` — an `IContentSource` ([Phase 83](83-icontentsource.md)) that turns the retrieval pipeline into **public/gated SSR answer pages**. A slug under a configured prefix (e.g. `/answers/how-do-i-deploy`) is resolved at request time via `IRetrievalPipeline.Retrieve` and rendered as a **cited `Narrative`**.

| Symbol | Where | Purpose |
|---|---|---|
| `RagAnswerSource.create` | `Server/RagAnswerSource.fs` | Build the content source over an `IRetrievalPipeline` |
| `RagAnswerConfig` (+ `create` / `withTopK` / `withMinScore` / `strictlyGrounded` / `withSynthesis`) | `Server/RagAnswerSource.fs` | Compose-time config |
| `RagSynthesisHook` | `Server/RagAnswerSource.fs` | Optional LLM-synthesis hook |
| `RagAnswerSource.queryFromSlug` | `Server/RagAnswerSource.fs` | Slug-tail → query helper |

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

## Verification

- `dotnet run --project Build.fsproj -- VerifyAll` — the `KnowledgeSurface (Phase 91)` suite (7 tests) covers `queryFromSlug`, the extractive cited answer, **StrictlyGrounded refusal** (incl. refuse-despite-synthesis-hook and MinScore-drops-weak-match), synthesis-hook prose, and prefix fall-through.

## Deferred (roadmap follow-ons)

KB-as-docs SSR surface, the `/search?q=` semantic-search endpoint, and the `publish_narrative` template/guardrail/forced-Draft hardening remain open in [roadmap Phase 91](../../) and will land as follow-on commits.

## See also

- [`docs/migrations/83-icontentsource.md`](83-icontentsource.md) — the content-source seam.
- [`docs/migrations/84-ssr-render-cache.md`](84-ssr-render-cache.md) — caching expensive answer pages.
- [`docs/platform/gated-ssr.md`](../platform/gated-ssr.md) — gating a knowledge portal to internal audiences.
