# Knowledge portal — RAG / KB content surfaces

`ToolUp.PublicRendering` can turn a deployment's retrieval pipeline and
knowledge base into **public (or gated) server-rendered pages**: grounded
answer pages, a browsable document collection, a semantic-search page, and
an FAQ — all SEO-indexable, all cacheable, all opt-in. This is the
"knowledge portal" surface built on the [dynamic-SSR](dynamic-ssr.md)
content-source seam.

Every surface here is **off by default** (GP 11) and **costs nothing when
not composed** (GP 13). None of them pull a knowledge-base or RAG-companion
dependency into `ToolUp.PublicRendering` — each consumes a Platform-level
interface (`IRetrievalPipeline`) or a small **deployment-supplied read-port**
(GP 1), so the public-rendering package's dependency graph stays minimal.

## The four surfaces

| Surface | Type | Serves | Backed by |
|---|---|---|---|
| RAG answer pages | `RagAnswerSource` (`IContentSource`) | `/<prefix>/<question>` | `IRetrievalPipeline` |
| KB-as-docs | `KnowledgeDocsSource` (`IContentSource` + `IEnumerableContentSource`) | `/<prefix>` index + `/<prefix>/<id>` landings | `IKnowledgeDocsProvider` (read-port) |
| Semantic search | `SemanticSearchHandler` (Giraffe handler) | `/search?q=` | `IRetrievalPipeline` |
| Suggested-questions FAQ | `SuggestedQuestionsSource` (`IContentSource`) | `/<faqPrefix>` | `SuggestedQuestionsProvider` (read-port) |

Plus **AI authoring hardening** (`NarrativePublishGuardrails`) on the
`publish_narrative` path — not a page surface, but the safety layer for
AI-driven page creation.

## 1. Grounded answer pages (`RagAnswerSource`)

A slug under a configured prefix (e.g. `/answers/how-do-i-deploy`) is
de-slugified into a query, run through `IRetrievalPipeline.Retrieve` under
the request's `AccessContext`, and rendered as a **cited `Narrative`**.

- **Extractive by default** — the answer is the retrieved chunks rendered
  as cited blockquotes, grounded *by construction*: every word shown came
  from a retrieved source. No hallucination surface, no LLM call.
- **Optional synthesis** — `withSynthesis` wires the deployment's own
  `IAIProvider`-backed prose generator; the SDK never calls an LLM itself.
- **`strictlyGrounded`** — a query with no match at/above `MinScore`
  renders a graceful **no-answer page** rather than speculative prose,
  *even when a synthesis hook is present*.

```fsharp
let answers =
    RagAnswerSource.create pipeline (
        RagAnswerConfig.create "answers" [ VectorScope.Team teamId ]
        |> RagAnswerConfig.withMinScore 0.35
        |> RagAnswerConfig.strictlyGrounded)
```

## 2. KB-as-docs (`KnowledgeDocsSource`)

Renders a document collection as browsable pages: a **collection index**
(`/<prefix>`) listing every document, and a **per-document landing**
(`/<prefix>/<id>`) whose body is the document's extraction-derived
structure (per page / slide / section), each section anchored on its
location hint for deep-linking.

The document data arrives through an `IKnowledgeDocsProvider` the
deployment fills from its own knowledge base — so `ToolUp.PublicRendering`
never references a knowledge-base companion (GP 1). The provider receives
the request `AccessContext`, so it returns only documents the caller may
read (GP 4). The source also implements `IEnumerableContentSource`, so its
pages appear in `sitemap.xml` and the static export.

```fsharp skip=fragment
let docsProvider =
    { new IKnowledgeDocsProvider with
        member _.ListDocuments ctx = async { (* map your KB's GetDocuments *) ... }
        member _.GetDocument id ctx = async { (* map extracted structure *) ... } }

let docs = KnowledgeDocsSource.create docsProvider (KnowledgeDocsConfig.create "docs")
```

## 3. Semantic search (`/search?q=`)

`withSemanticSearch` mounts a server-rendered search page that runs
`IRetrievalPipeline.Retrieve` (resolved from DI) under the request scope
and renders a ranked, indexable result list — distinct from any in-app AI
panel. Each result can deep-link into a KB-as-docs landing via an opt-in
link builder.

- **Query-size cap** — `MaxQueryChars` (default 16384) refuses an
  over-long query with a `400` rather than truncating it at the provider
  boundary.
- **Rate limiting** — the `/search` route rides the deployment's *general*
  rate-limit partition (it's mounted on the SDK route chain); no bespoke
  limiter.
- **Decline-on-no-RAG** — even when composed, the route falls through to a
  404 when no `IRetrievalPipeline` is registered.

```fsharp skip=fragment
|> PublicRenderingServerApp.withSemanticSearch (
    SemanticSearchConfig.create [ VectorScope.Team teamId ]
    |> SemanticSearchConfig.withResultLink (SemanticSearch.docsLinkByChunkId "docs"))
```

## 4. Suggested-questions FAQ (`SuggestedQuestionsSource`)

An FAQ index that seeds the answer pages: each suggested question links to
`/<answersPrefix>/<slug>`, where the slug round-trips through
`RagAnswerSource.queryFromSlug` back to the question text, so a click lands
on the grounded answer page. Questions arrive through a
`SuggestedQuestionsProvider` the deployment fills from its knowledge base
(GP 1, GP 4).

```fsharp
let faq =
    SuggestedQuestionsSource.create
        (fun ctx -> kb.GetSuggestedQuestions None)   // your KB's suggestions
        (SuggestedQuestionsConfig.create "faq" "answers")
```

## AI authoring hardening (`NarrativePublishGuardrails`)

The `publish_narrative` AI tool writes to the public page surface. The
guardrails let a deployment constrain that path:

- **forced-Draft landing** — every AI publish lands as
  `PublishStatus.Draft` ([CMS authoring](cms-authoring.md) review workflow),
  so it is **not publicly served until a human reviews it**;
- **layout allow-list** — an explicit `layoutHint` outside the approved
  set is refused;
- **forced audience** — the published page's [audience](gated-ssr.md) is
  pinned so AI can never widen reach;
- **template shape cap** — documents exceeding `MaxSections` are refused.

Defaults reproduce the prior immediate-publish behaviour (GP 11);
`NarrativePublishGuardrails.aiHardened` is the recommended opt-in for
multi-tenant / untrusted-AI deployments.

```fsharp skip=fragment
|> PublicRenderingServerApp.withAIPublishEnabled true
|> PublicRenderingServerApp.withAIPublishGuardrails (
    NarrativePublishGuardrails.aiHardened
    |> NarrativePublishGuardrails.withAllowedLayouts [ "article" ])
```

## Putting it together

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withContentSource answers
    |> PublicRenderingServerApp.withContentSource docs
    |> PublicRenderingServerApp.withContentSource faq
    |> PublicRenderingServerApp.withSemanticSearch searchConfig
    // answer / search pages are expensive — cache them (Phase 84):
    |> PublicRenderingServerApp.withRenderCache (InMemoryRenderCache.create ())
    |> PublicRenderingServerApp.withRenderCacheDefaultPolicy (Cache(300, true)))
|> ServerApp.run
```

Gate any of these to internal audiences by combining with
[`audience:` gating](gated-ssr.md) — the same `AccessContext` that scopes
retrieval also gates page visibility.

## See also

- [dynamic-ssr.md](dynamic-ssr.md) — the `IContentSource` request-time seam.
- [gated-ssr.md](gated-ssr.md) — audience gating for internal knowledge portals.
- [cms-authoring.md](cms-authoring.md) — the draft → review → publish lifecycle AI drafts route through.
- [`docs/migrations/91-knowledge-surfaces.md`](../migrations/91-knowledge-surfaces.md) — the adoption / migration guide.
