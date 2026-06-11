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

## Analytics → page body — `NarrativeFromData` (Phase 85)

A content source returns a `ContentBody`; the interesting work is turning backend data into the `Narrative` body without hand-rolling HTML. `NarrativeFromData` is the projector library for exactly that: pure functions from `ProcessedData` / `FileListSnapshot` / rows + KPIs into `NarrativeElement` trees that render to HTML/Markdown/plaintext/Atom through the existing Phase 80 renderers.

Every projector is pure (GP 5) and formats with `InvariantCulture`, so a prerender pass and a request-time render produce byte-identical output regardless of server locale (the [prerender determinism](prerender.md) rule).

### A client-reporting page, end to end

```fsharp
open ToolUp.PublicRendering
open ToolUp.Platform.Narrative

let clientReport =
    ContentSource.ofRoute "reports/{client}" (fun captures ctx -> async {
        let client = captures.TryFind "client" |> Option.defaultValue ""
        match! loadCampaign ctx client with          // ctx scopes the query (GP 4)
        | None -> return None                         // unknown {client} → fall through
        | Some c ->
            let body =
                [ NarrativeFromData.metricGrid
                    [ "Spend",       c.SpendDisplay,  Some c.SpendDelta      // ▲ +23.0%
                      "Conversions", c.ConvDisplay,   Some c.ConvDelta ]     // ▼ -4.0%
                  NarrativeFromData.thresholdCallout
                    NarrativeFromData.spendThresholds c.SpendDelta
                    (sprintf "Spend is %s vs target." c.SpendVsTargetLabel)   // severity by ladder
                  NarrativeFromData.table
                    [ "Channel", TableAlignment.Left; "Spend", TableAlignment.Right; "CPA", TableAlignment.Right ]
                    (c.Channels |> List.map (fun ch ->
                        [ CellText ch.Name; CellMoney(ch.Spend, "£"); CellMoney(ch.Cpa, "£") ])) ]

            let doc =
                Narrative.create (sprintf "%s — campaign report" c.Name)
                |> Narrative.section "Performance" "performance" body

            return Some (Narrative doc)
    })
```

`CellMoney` / `CellPercent` / `CellDate` format locale-stably; `metricGrid` emits up/down arrow styling hooks per delta; `thresholdCallout` maps a value through a `Threshold` ladder (`spendThresholds` is a shipped default) to a `Severity`.

### Charts (Phase 94)

`NarrativeFromData.chart` / `sparkline` project a labelled series into a `Component("chart", …)` block (the sanctioned Phase 87 type-erasure seam — no `NarrativeElement` DU fork). The HTML render is a **deterministic inline SVG** (no JavaScript → prerender-safe, byte-identical across runs) supplied by `NarrativeCharts`; a deployment opts in by including `NarrativeCharts.registry` in its component registry:

```fsharp
let body =
    [ NarrativeFromData.chart NarrativeFromData.Line (Some "Revenue trend")
        [ "Jan", 12_500.0; "Feb", 13_900.0; "Mar", 15_200.0 ]
      NarrativeFromData.metricGrid [ "MoM", "+9.4%", Some 0.094 ] ]

// render the page body with the chart renderer registered:
let html = NarrativeLayout.renderBodyWith Set.empty NarrativeCharts.registry page
```

Charts carry `tu-chart__*` BrandKit class hooks (style with your own CSS); no charting-vendor dependency ships in core (GP 2). Markdown / plaintext degrade to the standard `[component: chart]` placeholder — pair with `NarrativeFromData.chartTable "Month" "Revenue" series` for a real `Table` fallback in feeds / exports / print.

### Projecting a module's `ProcessedData` by type

When the body comes from a module's processed-data payload, register one projector per `TypeName` and route through `fromProcessed`. An unknown type degrades to a graceful callout (never an exception); a throwing projector is contained as a `Critical` callout, so one bad payload can't 500 the page.

```fsharp
let registry =
    NarrativeFromDataProjectors.empty
    |> NarrativeFromDataProjectors.registerTyped<SalesSummary> "SalesData" (fun s ->
        [ NarrativeFromData.table
            [ "Region", TableAlignment.Left; "Spend", TableAlignment.Right ]
            (s.Regions |> List.map (fun r -> [ CellText r.Name; CellMoney(r.Spend, "£") ])) ])

let opts = NarrativeFromDataProjectors.options registry

let dataPage =
    ContentSource.create (fun (Slug slug) ctx -> async {
        match! loadProcessed ctx slug with
        | Some processed ->
            let doc =
                Narrative.create "Data report"
                |> Narrative.section "Body" "body" (NarrativeFromData.fromProcessed processed opts)
            return Some (Narrative doc)
        | None -> return None
    })
```

`registerTyped<'T>` folds the `System.Text.Json` decode (the SDK's F#-aware converter set) into registration, so the projector body works with a typed value. `NarrativeFromData.fromFileSnapshot` covers the data-room shape (a processed-file status table) the same way.

### Optional AI executive summary

A projector's pure-data output can be prefaced by an AI-generated summary when a deployment composes one. The SDK ships no implementation and takes no dependency on the AI substrate — the hook is a plain function; absent one the pure-data path is used unchanged (GP 13):

```fsharp
let body =
    NarrativeFromData.fromProcessed processed opts
    |> NarrativeFromData.withSynthesis NarrativeFromData.withoutSynthesis   // or your hook
```

## Making dynamic pages discoverable (Phase 95)

Request-time source pages (and the `/tag/{x}` taxonomy pages) are computed per request, so the context-free `ListPages` never enumerates them — which means by default they're invisible to `sitemap.xml`, the static-export build, and prerender. A source opts into discovery by also implementing `IEnumerableContentSource`:

```fsharp
let dynamic =
    ContentSource.ofRouteEnumerable "report/{client}"
        (fun captures ctx -> async { (* … resolve … *) })
        (fun () -> async { return knownClients |> List.map (fun c -> Slug ("report/" + c)) })  // enumerate
```

The shipped `TaxonomyHandler.tagIndexSource` already does this (it enumerates one `/tag/{slug}` per distinct tag). `sitemap.xml` and `StaticExport` call `ContentSource.enumerateAll` over the registered sources and fold the enumerated slugs in (deduped against the file/overlay pages). A source that doesn't implement `IEnumerableContentSource` is unaffected — its pages stay request-only (GP 11). Enumerate **only public** slugs; a source that gates some pages omits them, so nothing private reaches a crawler or the static build.

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
