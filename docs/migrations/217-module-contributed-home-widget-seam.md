# Phase 217 — module-contributed home-widget seam

**Forge commits:** `e9c7057` (client seam + render + tests), `a99ad53` (server widget-data seam), `87b6180` (per-user recents/pinning)

Composes additively onto [Phase 171](171-home-overview-module.md). A deployment
that never registers a contributor sees Home render **byte-for-byte as Phase
171** (GP 13).

## What changes

A module can now surface its own widget (a chart, a recents list, a
call-to-action) on the Phase 171 Home / Overview landing surface **without
`Platform.Client` ever naming the contributing module** (GP 9). Three additive
surfaces, all off by default:

- **`ToolUp.Platform.Client`** — `HomeWidget` / `HomeWidgetContext` /
  `IHomeWidgetContributor` (in `SDK.ClientTypes.fs`), a new
  `ClientHandlerRegistry.HomeWidgetContributors` field (default `[]`), the
  boot-populated `HomeWidgetRegistry`, and the render in `Home.fs` (contributed
  widgets below the built-in tool / Active-AI / deployment cards, sorted by
  `Weight`). Duplicate widget ids fail loud at `Client.run`.
- **`ToolUp.Platform.Core`** — an additive `HomeOverview.WidgetData : Map<string,
  string>` bag (empty default) plus the optional `IHomeWidgetDataProvider` DI
  seam.
- **`ToolUp.Platform.Server`** — `HomeOverviewApiHandler` resolves every
  registered `IHomeWidgetDataProvider` as a DI collection, runs them in parallel
  for the caller's resolved scope (GP 4), and merges their maps into
  `WidgetData` — so widget data rides the single `GetOverview` call, not a
  second round trip.

### The contributor

```fsharp
open ToolUp.Platform
open Feliz

// A module exports its contributor value.
let homeWidget : IHomeWidgetContributor =
    { new IHomeWidgetContributor with
        member _.Widgets () = [
            { Id     = "sales.headline"
              Title  = "Sales this month"
              Icon   = Html.none
              Weight = 10
              Body   = fun ctx ->
                  // Scope-correct server data arrives in ctx.Data, keyed by
                  // this widget's namespace (populated by the matching
                  // IHomeWidgetDataProvider). Click-through reuses the shell
                  // navigation hook — no new primitive.
                  Html.button [
                      prop.onClick (fun _ -> NavigationRequest.request "SalesAnalysis")
                      prop.text (ctx.Data |> Map.tryFind "sales.headline.total" |> Option.defaultValue "—")
                  ] }
        ] }
```

### Wiring (consumer composition root)

```fsharp
// Client — register the contributor (additive; order-independent).
let clientConfig =
    { ClientConfig.defaults with
        HomeModule = EnabledHomeModule
        Handlers =
            { ClientConfig.defaults.Handlers with
                HomeWidgetContributors = [ MyModule.homeWidget ] } }
```

```fsharp
// Server — OPTIONAL: only if a widget needs scope-correct server data.
// Register an IHomeWidgetDataProvider in DI; the handler merges every
// provider's map into HomeOverview.WidgetData automatically.
services.AddSingleton<IHomeWidgetDataProvider>(MyModule.salesWidgetData) |> ignore
```

## Diff to apply

**Additive and opt-in — existing consumers need no change.** No contributor ⇒
`HomeWidgetRegistry.widgets ()` is empty ⇒ the contributed section renders
`Html.none` and `WidgetData` is empty, so Home is identical to Phase 171.

A consumer that wants a widget: (1) export an `IHomeWidgetContributor` from the
module, (2) add it to `ClientConfig.Handlers.HomeWidgetContributors`, and
(3) — only when the widget needs server data — register an
`IHomeWidgetDataProvider` in server DI, keying its values under the widget's id
namespace.

## Conventions

- **Namespacing.** `HomeWidget.Id` is the React key, must be unique across all
  contributors (enforced at `Client.run`), and is the prefix convention for
  `HomeWidgetContext.Data` keys (e.g. `"sales.headline.total"`). The data bag is
  shared across every widget, so namespacing avoids collisions; on a key
  collision the last provider wins.
- **Weight.** Widgets render in ascending `Weight`; ties keep registration order
  (stable sort).
- **Navigation.** Widget bodies call `NavigationRequest.request` (Phase 6g.C)
  directly — no new navigation primitive, no dependency on the shell `Msg` type.

## Recents / pinning (sub-feature, `87b6180`)

A built-in **"Pinned / Recent"** Home widget, opt-in via
`ClientConfig.HomeRecents = true` (default `false`, GP 13). It stores a small
per-user set of recently-visited + pinned tool ids, persisted through the
existing `IConfigStore` seam under a reserved `_sdk.home.pinning` key.

- **Per-user, never cross-scope (GP 4).** Persisted in the caller's *user*
  scope even in Team mode, so a team-mate never inherits another member's
  recents. `IHomeOverviewApi` gains three `[<RequiresClaim "scope">]` methods:
  `GetPinning` / `RecordVisit` / `SetPinned`.
- **Recents** are most-recent-first, deduped, bounded (8). A visit is recorded
  when a tool is opened from the Home surface; click-through reuses
  `NavigationRequest.request` (no new primitive).
- **Default-off parity.** With `HomeRecents = false` the Home module makes
  exactly the Phase 171 call set (no `GetPinning`) and renders no widget.

```fsharp
let clientConfig =
    { ClientConfig.defaults with
        HomeModule  = EnabledHomeModule
        HomeRecents = true }     // opt in to the Pinned / Recent widget
```

No server wiring is needed — the recents/pinning routes ride the auto-mounted
`IHomeOverviewApi` and persist through the default `IConfigStore`.

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive; build stays green.
2. Client-tier Fable harness `ToolUp.AI.Client.Tests/HomeWidgetContributorTests.fs`
   — zero contributors, default-off, single contributor, weight order, stable
   ties, last-wins re-set.
3. In-process `ToolUp.Platform.Tests/InProcess/HomeOverviewTests.fs` (Phase 217
   lists) — multiple `IHomeWidgetDataProvider`s merge scope-correctly; no
   provider ⇒ empty bag; recents/pinning round-trip + cross-user +
   cross-team-member isolation.
4. With no contributor registered and `HomeRecents = false`, confirm Home is
   byte-for-byte the Phase 171 surface (GP 13).

## Rollback

Additive throughout. Revert the two forge commits; consumers that never register
a contributor or provider are unaffected (the `HomeOverview.WidgetData` field is
the only wire-shape change and defaults to empty).
