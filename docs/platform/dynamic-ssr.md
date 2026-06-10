# Data-bound SSR — `IContentSource`

`ToolUp.PublicRendering` resolves a slug to a page through a tiered chain. Phase 83 adds the final tier: **request-time content sources** that compute a page body from backend data instead of loading it from a file. This is what turns the companion from a brochure/docs engine into a data-bound application surface — analytics pages, KB-backed docs, dynamic CMS content.

## The resolution chain

```
GET /{slug}
   │
   ▼
PublicPageHandler                       resolve AccessContext from RequestServices
   │                                    (anonymous fallback when no auth)
   ▼
IPublicContentApi.GetPageInContext(slug, ctx)
   │
   ├─ 1. File tier      — MarkdownContentLoader (in-memory .md map)         ─┐
   ├─ 2. Overlay tier   — IEntityStore<PublicPage> (runtime-edited content)  │ first Some wins
   └─ 3. Source tier     — IContentSource list, in registration order        ─┘
                            each: Resolve : Slug -> AccessContext -> ContentBody option
   │
   ▼
PublicPage (synthesised around the resolved ContentBody)
   │
   ▼
layout page → Giraffe.ViewEngine → HTML
```

Tiers 1 and 2 are unchanged from Phase 38. Tier 3 is consulted **only** when both file and overlay miss, and **only** on `GetPageInContext` — the context-free `GetPage` / `ListPages` / `GetCollection` methods (used by sitemap generation and static export) never enumerate source-produced pages, which are dynamic by nature.

**Zero cost when unused (GP 11 / GP 13).** A deployment that registers no sources resolves through `GetPageInContext` to exactly what `GetPage` returns — byte-for-byte identical to the pre-83 chain. The source loop over an empty list is the only added work.

## Writing a content source

A source is a function `Slug -> AccessContext -> Async<ContentBody option>`. Return `Some body` to claim the slug, `None` to fall through to the next source (or, when every source declines, to the 404 path). Two constructors:

### `ContentSource.create` — claim by your own logic

```fsharp
open ToolUp.PublicRendering

let statusPage =
    ContentSource.create (fun (Slug slug) ctx -> async {
        if slug = "status" then
            let! doc = buildStatusNarrative ctx       // ctx scopes the query (GP 4)
            return Some (Narrative doc)
        else
            return None
    })
```

### `ContentSource.ofRoute` — claim a family of dynamic paths

A route pattern is a `/`-delimited template where `{name}` captures one segment. `"services/{client}"` matches `"services/acme"` capturing `client = "acme"`. Literal segments must match exactly; segment counts must be equal (captures do not span segments).

```fsharp
let clientPages =
    ContentSource.ofRoute "services/{client}" (fun captures ctx -> async {
        let client = captures.TryFind "client" |> Option.defaultValue ""
        match! lookupClient ctx client with
        | Some data -> return Some (Narrative (renderClientNarrative data))
        | None       -> return None      // unknown {client} → fall through
    })
```

`RouteShape.tryMatch pattern slug` is exposed directly if you need the matcher without the source wrapper.

## Registering sources

```fsharp
PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withContentSource statusPage
|> PublicRenderingServerApp.withContentSource clientPages   // order = resolution order
|> PublicRenderingServerApp.run
```

Multiple `withContentSource` calls compose; resolution is in registration order, first `Some` wins. The helper composes additively the same way `withLayout` / `withFeed` do.

> **`withContentApi` overrides ignore sources.** When you supply a complete `IPublicContentApi` via `withContentApi`, that impl owns its own resolution and registered sources are not consulted — the override is the resolution chain.

## Page metadata from data (SEO without a frontmatter file)

A source returns only a `ContentBody`; the page's presentation metadata is derived from it. For a `Narrative` body:

- `PublicPage.Title` ← `NarrativeDocument.Title`
- `PublicPage.Description` ← `NarrativeDocument.Subtitle`
- `PublicPage.PublishedAt` ← `NarrativeDocument.Provenance.GeneratedAt`

The `<head>` (canonical URL, Open Graph, Twitter card, schema.org JSON-LD) is then produced by the shipped `NarrativeLayout.headTags` helper off the same `Narrative` body — so a computed page is SEO-correct with no `.md` frontmatter. Set `NarrativeDocument.CanonicalUrl` (via `Narrative.withCanonicalUrl`) and stamp `Provenance` (via `Narrative.withProvenance`) for full metadata.

A layout that wants the data-driven head splices the helper output:

```fsharp
let pageLayout (page: PublicPage) : XmlNode =
    html [ ] [
        head [ ] [
            title [ ] [ encodedText page.Title ]
            yield! NarrativeLayout.headTags page          // canonical + OG + JSON-LD from the Narrative
        ]
        body [ ] [ NarrativeLayout.renderBody page ]
    ]
```

The synthesised page leaves `Layout` unset, so `PublicPageHandler` falls back to the first-registered layout. Layout selection stays a compose-time concern — a source picks the body, not the chrome.

## Scoping by principal (GP 4)

Every source receives the resolved `AccessContext`. The page handler resolves it from `ctx.RequestServices` (falling back to an unrestricted anonymous context when no auth is wired — the normal case for a public content site). Use it to scope a query structurally:

```fsharp
let dashboard =
    ContentSource.ofRoute "dashboard/{quarter}" (fun captures ctx -> async {
        // A TeamMember sees their team's figures; an AnonymousSession the public view.
        let scope = AccessContext.configScope ctx
        let! doc = buildDashboard scope (captures.TryFind "quarter")
        return Some (Narrative doc)
    })
```

Tenant isolation rides the context, not a "remember to filter" convention — the same structural guarantee the rest of the SDK enforces.

## Six portability rules

`IContentSource` is six-rule clean (it could be implemented by a distributed task framework): identity by value (`Slug` / `AccessContext` / `ContentBody`), async at the boundary, stateless between invocations (all per-request state arrives by parameter), no framework leak. See [`docs/platform/portability-rules.md`](portability-rules.md) and the `IContentSourceContract` conformance pack in `ToolUp.Platform.Tests`.

## See also

- [`docs/platform/prerender.md`](prerender.md) — the two SSR surfaces (`IPublicContentApi` full SSR vs build-time `PrerenderRoutes`).
- [`docs/migrations/83-icontentsource.md`](../migrations/83-icontentsource.md) — adoption diff + rollback.
- [`docs/migrations/80-narrative-publicrendering-integration.md`](../migrations/80-narrative-publicrendering-integration.md) — the `ContentBody.Narrative` body + `NarrativeLayout` helpers this builds on.
