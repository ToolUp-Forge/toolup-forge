# Migration — `IPublicContentApi.ListPagesPublic` (the public-content enumeration gate)

**Applies to:** any code that implements `IPublicContentApi`, or that enumerates pages for an
anonymous public surface.
**Breaking:** yes, for external implementers — one member added to a shipped interface.
**Not breaking:** for callers. Every existing method keeps its signature and its behaviour, and a
deployment whose pages are all `Status = Published` sees byte-identical output (GP 11).

## What changed

`IPublicContentApi` gains a **gated** enumeration beside the existing ungated one:

```fsharp
abstract ListPagesPublic: now: DateTimeOffset * prefix: string -> Async<PublicPage list>
```

It returns `ListPages prefix` filtered through `PublicPage.isPubliclyDiscoverable now` — the Phase 38
egress gate, which conjoins the **audience** axis (`PageAudience.Public`) and the **publish-status**
axis (`Published`, or `Scheduled` whose instant has passed). Those axes are orthogonal, and filtering
on either alone leaks: an unpublished page carries the default `Audience = Public`, so an
audience-only filter admits it.

Phase 38 applied that predicate at the five enumeration call sites it found. This phase moves it onto
the seam, so the safe set is what a surface gets by default and the raw set is a deliberate choice.

`now` is a parameter rather than a wall-clock read so a multi-step pass can pin one instant — a
static export must not have a `Scheduled` page enter the sitemap but miss the page write because the
clock crossed its publish instant mid-run.

## If you implement `IPublicContentApi`

Add one member. The whole body can be the shipped default:

```fsharp
    interface IPublicContentApi with
        member _.ListPages prefix = ...          // unchanged
        member _.GetPage slug = ...              // unchanged
        member _.GetCollection id = ...          // unchanged
        member _.GetPageInContext(slug, ctx) = ... // unchanged

        // NEW — Phase 632.
        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix
```

Object expressions work the same way — name the self identifier (`{ new IPublicContentApi with member this.ListPagesPublic(now, prefix) = ... }`).

**Override it only if your backing store can push the predicate into its own query** (a SQL / index
store filtering on status + audience server-side rather than materialising every page). Whatever you
do, two laws hold and are asserted by `IPublicContentApiContract`:

1. the result is a subset of `ListPages prefix`, and
2. every returned page satisfies `PublicPage.isPubliclyDiscoverable now`.

**Decorators delegate, they do not re-gate.** A wrapper (`NarrativeLayout.SelfCanonical.wrap` is the
in-tree example) should pass `ListPagesPublic(now, prefix)` straight through to its inner API, so an
implementation that pushed the predicate down keeps doing so.

## If you enumerate pages for a public surface

Move from:

```fsharp
let! pages = api.ListPages ""
let visible = pages |> List.filter (PublicPage.isPubliclyDiscoverable now)
```

to:

```fsharp
let now = DateTimeOffset.UtcNow          // or a pinned instant for a multi-step pass
let! pages = api.ListPagesPublic(now, "")
```

`PublicContentApi.listPagesPublicNow api prefix` is the wall-clock convenience for a single-step
caller.

**Keep calling `ListPages`** for an admin / authoring / preview surface that legitimately needs the
whole set — a CMS page list, a signed-preview route, an export tool run by an authenticated
operator. That is what the member is for; it is now the unusual choice rather than the default one,
not a deprecated one.

### Surfaces migrated in-tree (no consumer action required)

`SitemapGenerator.handlerWith` + `shardHandler`, `SearchIndexEmitter.handler`,
`NarrativeFeedHandler.handler` (Atom), `StaticExport`, the IndexNow universe thunks in
`PublicRenderingCompose` (default site + satellites), and `TaxonomyHandler.tagIndexSourceFromApi`.
Each also now pins **one** clock per pass rather than reading the wall clock per step.

### Caller-supplied lists — `NavTree` / `Pagination`

`NavTree.ofCollection`, `NavTree.ofCollectionPaged` and `Pagination.paginate` are pure projections
over a list the caller supplies. They deliberately do **not** gate: a nav tree is also rendered on
authenticated and preview surfaces, and these functions have no `AccessContext` with which to tell
the two apart. Their contract — previously unstated, now documented on each — is that **the caller
gates before projecting** when the result reaches an anonymous surface.

Gate *before* paginating, not after: gating a slice leaves the page count and the boundary positions
computed over the ungated set, which is itself an oracle over how much unpublished content exists.

## Semantic search (`/search?q=`) — no change, and why

`SemanticSearchHandler` answers from `IRetrievalPipeline` (the RAG vector index), not from
`ListPages`, so this gate never applied to it. Phase 38 left open whether unpublished pages were
*ingested* into that index. **They are not, and cannot be:** `ToolUp.PublicRendering` takes no
reference to `ToolUp.RAG.*` (it reaches retrieval through the `IRetrievalPipeline` interface in
`ToolUp.Platform.Server` — a read port, GP 1), and neither `ToolUp.KnowledgeBase.Server` (the only
in-tree enqueuer of ingestion jobs) nor `ToolUp.RAG.Server` (which drains the queue) references
`ToolUp.PublicRendering`, so neither can name the `PublicPage` type. The index holds knowledge-base
documents, notes and committed narratives — scope-gated at retrieval against the caller's
`AccessContext` (GP 4) — never public pages. Nothing to gate, nothing to re-index on unpublish.

If your deployment wires page ingestion itself, that is your gate to hold: ingest only
`ListPagesPublic` output, or ingest everything and filter at query time — the first is safer, since
an index holding unpublished text is a leak waiting for the next query surface.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 632"
```

`IPublicContentApiContract` gains three cases binding any implementation to the two laws above plus
the prefix filter. The pack also carries a **structural probe** — a source sweep asserting that no
public enumeration path inside `ToolUp.PublicRendering` reaches raw `ListPages`, with a short
allowlist of deliberate readers each carrying its reason. If you fork the module and add a surface
that enumerates ungated, that probe names your file and line rather than letting the leak ship.

## Rollback

Remove the member from the interface and from your implementation, and restore the per-call-site
`|> List.filter (PublicPage.isPubliclyDiscoverable now)` at each enumeration surface. Note what that
restores: the guarantee goes back to resting on every caller remembering to conjoin both axes, which
is the arrangement under which the `/tag/{slug}` taxonomy index shipped ungated for six phases.
