# Migration — Phase 83: `IContentSource` (request-time / data-bound SSR content)

**Status:** additive, opt-in. A deployment that registers no content sources is **byte-for-byte identical** to the pre-83 file + entity-overlay resolution chain (GP 11) and pays nothing (GP 13).

## What changes

`ToolUp.PublicRendering` gains a request-time content-resolution seam. Before Phase 83, `PublicContentApiImpl` resolved a slug to a `ContentBody` through a fixed two-tier chain:

1. File-backed markdown (`MarkdownContentLoader`).
2. Optional `IEntityStore<PublicPage>` overlay (runtime-edited content).

Phase 83 adds a **third tier**: an ordered list of `IContentSource` resolvers, consulted after the file + overlay tiers (registration order, first `Some` wins). A source computes a page body **per request** from backend data (analytics, entity queries, retrieval) rather than loading it from a file. This turns the companion from a brochure/docs engine into a data-bound application surface.

New public surface:

| Symbol | Where | Purpose |
|---|---|---|
| `IContentSource` | `Server/IContentSource.fs` | `Resolve : Slug -> AccessContext -> Async<ContentBody option>` |
| `ContentSource.create` | `Server/IContentSource.fs` | Build a source from a plain resolver |
| `ContentSource.ofRoute` | `Server/IContentSource.fs` | Build a route-shape source (`"services/{client}"`) |
| `RouteShape.tryMatch` | `Server/IContentSource.fs` | Slug-pattern matcher returning captured segments |
| `IPublicContentApi.GetPageInContext` | `Server/IPublicContentApi.fs` | Context-aware resolution (file + overlay + sources) |
| `PublicRenderingServerApp.withContentSource` | `Server/PublicRenderingCompose.fs` | Compose-time registration |

## Adopting it

Register one or more sources on the compose pipeline. Each `Narrative`-bodied result renders with a data-driven `<head>` via the existing `NarrativeLayout.headTags` / `prerenderMeta` helpers (title/description/OG/canonical derive from the `NarrativeDocument`, no frontmatter file needed).

```fsharp
open ToolUp.PublicRendering

// A source claiming the family /dashboard/{quarter}, scoped to the caller.
let dashboards =
    ContentSource.ofRoute "dashboard/{quarter}" (fun captures ctx -> async {
        let quarter = captures.TryFind "quarter" |> Option.defaultValue "current"
        // ctx is the resolved AccessContext — scope the query to the principal (GP 4).
        let! doc = buildDashboardNarrative ctx quarter
        return Some (Narrative doc)   // return None to fall through to the next source / 404
    })

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withContentSource dashboards
|> PublicRenderingServerApp.run
```

Or via the additive `withPublicRendering` extension on a base `ServerApp` (Phase 80c):

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withContentSource dashboards)
|> ServerApp.run
```

## Breaking change — external `IPublicContentApi` implementors

`IPublicContentApi` gains one abstract method:

```fsharp
abstract GetPageInContext: slug: string * ctx: AccessContext -> Async<PublicPage option>
```

If you supply a **custom** `IPublicContentApi` via `withContentApi`, add this method. The minimal forwarding shape (no own content sources) is:

```fsharp
member this.GetPageInContext(slug, _ctx) = (this :> IPublicContentApi).GetPage slug
```

The SDK default (`PublicContentApiImpl`) and the in-tree contract pack cover the common path. Context-free callers (sitemap generation, static export) keep calling `GetPage` and are unaffected — source-produced pages are dynamic and intentionally absent from build-time enumeration.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `PublicRendering` suite covers the new `IContentSource` contract pack (two bindings), chain ordering, fall-through, route-shape capture, the no-source byte-identical path, and page-metadata-from-data.
- A deployment with no `withContentSource` calls produces identical output to pre-83 — confirmed by the `GetPageInContext with no sources is identical to GetPage` test.

## Rollback

Remove the `withContentSource` registration(s) from the compose root. With no sources registered, `GetPageInContext` returns exactly what `GetPage` returns; the third tier is inert. The interface method and the `IContentSource.fs` file can remain in place harmlessly (no runtime cost when unused).

## See also

- [`docs/platform/dynamic-ssr.md`](../platform/dynamic-ssr.md) — the data-bound SSR pattern in depth.
- [`docs/platform/prerender.md`](../platform/prerender.md) — the two SSR surfaces overview (`IPublicContentApi` SSR vs build-time `PrerenderRoutes`).
- [`docs/migrations/80c-with-public-rendering-additive-composition.md`](80c-with-public-rendering-additive-composition.md) — the `withPublicRendering` additive composition seam.
