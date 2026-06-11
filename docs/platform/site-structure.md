# Site structure — navigation & taxonomy

`ToolUp.PublicRendering` ships flat slugs plus an optional single-valued `Collection`. Phase 90 adds the two structural primitives an intranet or docs site lives or dies on: a **navigation tree** (multi-level menus, breadcrumbs, audience-gated items) and a **taxonomy** (multi-valued tags, tag-index pages, related content, faceted browse). Both are additive (GP 11) — a site that defines no nav and no tags renders exactly as before.

## Navigation tree

A `NavNode` is an ordered, nestable record — pure data (no functions, no live handles), so a tree round-trips through `nav.yaml` or an entity overlay and renders deterministically.

```fsharp
type NavTarget =
    | NavSlug of Slug      // internal page → /slug
    | NavUrl of string     // external/absolute URL, verbatim
    | NavSection of string // a section/collection id → /section

type NavAudience =
    | NavPublic            // everyone (default)
    | NavAuthenticated     // any non-anonymous subject
    | NavTeamOnly          // TeamMember subjects only

type NavNode = { Label: string; Target: NavTarget; Children: NavNode list; Audience: NavAudience }
```

Build a tree in code with the constructors, or load it from `nav.yaml`:

```fsharp
open ToolUp.PublicRendering

let nav =
    [ NavTree.leaf "Home" "home"
      NavTree.node "Services" (NavSection "services")
        [ NavTree.leaf "Consulting" "services/consulting" ]
      NavTree.leaf "Members" "members" |> NavTree.withAudience NavAuthenticated ]

// or, from a nav.yaml content file:
let nav = NavTree.parseYaml (System.IO.File.ReadAllText "content/nav.yaml")
```

`nav.yaml` is a small documented subset (2-space indent, `label` + a target + optional `audience` + nested `children`) — hand-parsed, no `YamlDotNet` dependency (GP 1):

```yaml
- label: Home
  slug: home
- label: Services
  slug: services
  children:
    - label: Consulting
      slug: services/consulting
    - label: Partners
      url: https://example.com
      audience: authenticated
```

### Audience filtering (GP 4)

`NavTree.filter ctx nav` drops every node the requesting principal can't see — together with its whole subtree — *before* render, so a gated item never appears in the markup for an unauthorised viewer. The audience model is evaluated against the shipped `AccessContext`, so site structure is self-contained; it doesn't depend on page-level audience targeting.

```fsharp
let visible = NavTree.filter ctx nav   // ctx from the request
```

### Rendering menus & breadcrumbs

`NavLayout` produces Giraffe.ViewEngine fragments a layout composes, each carrying stable `tu-nav__*` / `tu-breadcrumb__*` BrandKit class hooks (style them with your own CSS / CSS-variables — the SDK ships no opinionated styling). `menu` highlights the current page (`tu-nav__item--current` + `aria-current="page"`) and emits multi-level dropdown markup; `menuFor` filters by audience inline.

```fsharp
let pageLayout (page: PublicPage) (ctx: AccessContext) : XmlNode =
    html [] [
        body [] [
            NavLayout.menuFor ctx (Some page.Slug) nav            // audience-filtered + current highlight
            NavLayout.breadcrumb (NavTree.breadcrumbFromSlug page.Slug)
            // ... page body ...
        ]
    ]
```

`breadcrumbFromSlug` derives a trail from a nested slug — `"services/consulting"` → `[ ("services", "/services"); ("consulting", "/services/consulting") ]`. `findTrail slug nav` instead walks the nav tree to the matching node (root-first), for active-branch highlighting.

### Auto-nav from a collection

`NavTree.ofCollection pages` turns a collection's pages into a flat leaf menu (the docs-site "chapter list from the `docs` collection" shape):

```fsharp
let! chapters = api.GetCollection "docs"            // Phase 38
let docsMenu = NavTree.ofCollection chapters         // one NavSlug leaf per chapter, labelled by Title
```

## Taxonomy (tags)

Tags generalise the single-valued `Collection` into a multi-valued taxonomy. They are **frontmatter-derived** — a page's tags come from a comma-separated `tags:` frontmatter key — rather than a dedicated `PublicPage` record field, so every existing page-construction site is unaffected (GP 11). A markdown author writes:

```markdown
---
title: Launch announcement
tags: news, product, launch
---
```

and the helpers read them:

```fsharp
PublicPage.tags page          // [ "news"; "product"; "launch" ]
PublicPage.hasTag "Product" page   // true (case-insensitive)
```

### Tag-index pages

`TaxonomyHandler.tagIndexSource` is an [`IContentSource`](../platform/dynamic-ssr.md) that serves `/tag/{slug}` pages listing every page carrying that tag, as a `NarrativeDocument` (rendered through the existing Phase 80 renderers — no hand-rolled markup). It composes through the existing `withContentSource` seam:

```fsharp
PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withContentSource (TaxonomyHandler.tagIndexSource (fun () -> api.ListPages ""))
|> PublicRenderingServerApp.run
```

The provider thunk (`fun () -> api.ListPages ""`) enumerates the candidate set from the file + entity-overlay tiers (never the source tier, so no resolution recursion). An unknown tag degrades to a thoughtful empty-state body, not a 404. A non-`tag/...` slug falls through without invoking the provider.

### Related content & faceted browse

```fsharp
let related = TaxonomyHandler.relatedByTag allPages page   // same-tag pages, ranked by shared-tag count, self excluded
let facets  = TaxonomyHandler.tagCounts allPages           // [ ("news", 12); ("product", 8); … ] count-desc, tag-asc
```

`relatedByTag` is the pure-data path; a deployment that composes RAG can layer semantic-related on top. `tagCounts` drives a faceted-browse sidebar (case-insensitive grouping, deterministic order).

## Compose ergonomics (Phase 100)

`withTaxonomy` enables the `/tag/{slug}` surface with no hand-wired `listPages` thunk — compose registers `TaxonomyHandler.tagIndexSource` against the default content API automatically. `withNav` loads a `nav.yaml` file at startup and registers the parsed tree as a `NavCatalog` DI singleton (also stored on `app.Nav` for a layout to capture); `withNavTree` registers a code-built tree directly.

```fsharp
|> PublicRenderingServerApp.withTaxonomy
|> PublicRenderingServerApp.withNav "content/nav.yaml"
```

## Faceted browse (Phase 99)

`TaxonomyHandler.facetedBrowseSource` serves `/browse/{tags}` where `{tags}` is a `+`-separated tag list (AND semantics) — the "filter by topic + type" interaction. It renders the matching pages plus a **facet sidebar** (each remaining tag + the count it would yield, narrowing as tags are added), excluding gated and non-published pages (GP 4). The pure `TaxonomyHandler.facetedBrowse matchAll tags pages` (AND when `matchAll`, OR otherwise) returns `(results, facets)` for custom wiring, and composes with the Phase 98 pager.

```fsharp
|> PublicRenderingServerApp.withContentSource (TaxonomyHandler.facetedBrowseSource (fun () -> api.ListPages ""))
// GET /browse/news+product  → pages tagged both, + facet counts
```

## Pagination (Phase 98)

`Pagination.paginate pageSize page items` is a pure helper returning the page's slice plus a stable `{ Page; PageCount; HasPrev; HasNext }` contract (`pageSize <= 0` = the whole list, unchanged). `NavTree.ofCollectionPaged` paginates a collection menu; `NavLayout.pager pageHref slice` renders a `Previous` / numbered / `Next` control with `tu-pager__*` class hooks — `pageHref n` lets the layout choose the URL scheme (path `/tag/news/2` or query `?page=2`):

```fsharp
let slice = NavTree.ofCollectionPaged 20 page chapters
NavLayout.menu None slice.Items
NavLayout.pager (sprintf "/docs?page=%d") slice
```

## Per-tag Atom feeds (Phase 97)

A reader can subscribe to a topic: `withTagFeed` registers a feed filtered to one tag (the taxonomy-axis extension of the Phase 80b `withFeed`), reusing the same Atom renderer:

```fsharp
|> PublicRenderingServerApp.withTagFeed "news"
     { NarrativeFeedConfig.defaults with Title = "News"; SelfUrl = "/tag/news/feed.atom" }
// or fan out one feed per tag:
|> PublicRenderingServerApp.withTagFeeds "Topics" NarrativeFeedConfig.defaults [ "news"; "events"; "product" ]
```

A tag feed surfaces the Narrative-bodied pages carrying that tag, newest-first, capped at `MaxEntries`; gated pages never appear. A deployment that registers no tag feed emits nothing (GP 11). The `Tag` filter composes with `Collection` (both apply).

## Structured data (JSON-LD) — Phase 96

`NavLayout.headStructuredData baseUrl ctx currentSlug nav` derives schema.org `BreadcrumbList` (from the page's nested slug) and `SiteNavigationElement` (from the **audience-filtered** nav tree) JSON-LD, absolutised against the base URL, as `<script type="application/ld+json">` blocks a layout splices into `<head>`:

```fsharp
head [] [
    // …
    yield! NavLayout.headStructuredData config.PublicBaseUrl ctx (Some page.Slug) nav
]
```

The site-nav structured data omits the same gated items the rendered menu omits (GP 4 — no leak), and returns `[]` when there's nothing to describe, so a layout `yield!`s it unconditionally.

## See also

- [`docs/platform/dynamic-ssr.md`](dynamic-ssr.md) — the `IContentSource` seam the tag-index serves through, and `NarrativeFromData` (the same "data → Narrative body" pattern the tag-index uses).
- [`docs/platform/cms-authoring.md`](cms-authoring.md) — the CMS authoring layer nav/taxonomy soft-composes with.
