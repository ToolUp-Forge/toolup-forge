# Migration 276 — hosted-tree navigation/route contract

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

Phase 267 lets a hosted typed-tree drive a multi-page module, and Phase 110's `Navigate` reaches the
shell router — but a hosted module's pages were **not registered into the public SSR route table**
(Phase 111), so they had no stable URL, no deep link, and no crawlability. This phase ships one
**neutral route type** read by both tiers, so a hosted multi-page module is a first-class routed
surface. No tree-language type appears (GP 1).

New surface:

- `src/ToolUp.Platform.Core/Shared/HostRoutes.fs` (namespace `ToolUp.Platform`) — the **neutral route
  declaration**, in the shared floor because both tiers consume it (GP 10):
  - `HostRoute = { PathPattern; SidebarId; Title }` — a path pattern (`"reports/{quarter}"`,
    single-segment `{name}` capture), the `NavigationRequest.SidebarId` it deep-links to, and a title.
  - `HostRouteParams = Map<string,string>` — captured segments (same shape as Phase 111 `RouteShape`).
  - `HostRoute.tryMatch` / `HostRoute.paramsToBindingSources` — the matcher (same single-segment rule
    as the SSR `RouteShape`, duplicated because Core can't reference `ToolUp.PublicRendering`) and the
    projection of captured params into a `HostBindingSources.State` namespace (the Phase 264 param
    round-trip).
- `src/ToolUp.Platform.Client/Client/HostRouteContract.fs` (namespace `ToolUp.Platform`) — the
  **client half**:
  - `IHostRouteContract` (`Routes`, `TryResolve`) built via `HostRouteContract.create routes`.
  - `HostRouteContract.navigate` / `deepLink` — fire `NavigationRequest` for a resolved route and
    return the route's param state as a `HostBindingSources` (deep-link + back-button + param restore).
- `src/ToolUp.PublicRendering/Server/HostRouteRegistration.fs` (namespace `ToolUp.PublicRendering`) —
  the **SSR half**:
  - `HostRouteResolver = HostRouteParams -> AccessContext -> Async<ResolvedContent option>` and a
    `HostRouteRegistration` record (`Route` / `Resolve` / `Enumerate`).
  - `HostRouteRegistration.toContentSource` maps a registration to an `IContentSource` +
    `IResolvedContentSource` + `IEnumerableContentSource` (via `ContentSource.ofRouteResolvedEnumerable`),
    so the page is SEO-complete and its concrete slugs reach `sitemap.xml` / static export / prerender
    (crawlable).
  - `HostRouteRegistration.register` folds each registration into a `PublicRenderingServerApp` via the
    Phase 83 `withContentSource` (append-only — GP 11).

Both halves read one neutral route type, so a route resolves identically client-side and server-side.
A pipeline that declares no host routes is byte-for-byte unchanged (GP 11) and pays nothing (GP 13).

## How to adopt (opt-in)

Client — declare routes + deep-link:

```fsharp
let contract =
    HostRouteContract.create [
        { PathPattern = "reports/{quarter}"; SidebarId = "Reports/quarter"; Title = "Quarterly Report" }
    ]

// on a deep-link URL (browser back/forward, direct navigation):
match HostRouteContract.deepLink contract "reports/q3" with
| Some sources -> MyTreeRuntime.render (page ()) host sources   // params restored via Phase 264
| None -> render404 ()
```

SSR — register the same routes into the public route table:

```fsharp
PublicRenderingServerApp.create ()
|> HostRouteRegistration.register [
    HostRouteRegistration.create
        { PathPattern = "reports/{quarter}"; SidebarId = "Reports/quarter"; Title = "Quarterly Report" }
        (fun captures ctx -> async { return Some(ResolvedContent.ofBody (ContentBody.Html (renderReport captures))) })
        (fun () -> async { return knownQuarterSlugs () })   // crawlable slugs for the sitemap
   ]
|> PublicRenderingServerApp.run
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
cd samples/MinimalClient && dotnet fable -o output   # client-tier contract compiles under Fable
```

`HostRouteContractTests` proves: a declared route deep-links with params restored; the route
registers into the SSR table (matching slug resolves, non-matching falls through) and is enumerable
(crawlable); back/forward navigation is consistent; the toy (a stranger tree language) resolves a
`Bind` against the restored params; `register` appends append-only; the seams carry no banned OSS
vocabulary.

## Rollback

Delete `HostRoutes.fs` / `HostRouteContract.fs` / `HostRouteRegistration.fs` + their `<Compile>`
entries; delete `InProcess/HostRouteContractTests.fs` + its `<Compile>` and `Program.fs`
registration. No runtime impact on any deployment that never declared a host route.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in hosted-tree routing surface. No
current matrix consumer hosts a typed-tree UI; a deployment that declares no routes is byte-for-byte
unchanged (GP 11/13).
