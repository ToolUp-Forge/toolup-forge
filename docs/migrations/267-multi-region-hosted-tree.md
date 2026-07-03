# Migration 267 — multi-region / `PageContent` hosted-tree composition

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

The Phase 110 `ClientHostView.withElementView` overload hosts a hosted typed-tree in exactly **one
full-width region**. A hand-authored Feliz `ClientModule` can be a split-pane (`withView` returns a
`ReactElement * ReactElement` control/output tuple → `PageContent.SplitPanel`) or a multi-page module
(`withPages` returns a `PageContent` case per page). A hosted tree could be neither — the single
biggest *layout* limitation of the hosting seam. This phase adds two additive overloads that map a
hosted tree onto the **existing** layout shapes, so a hosted module is a first-class layout peer of a
hand-authored one. No new `PageContent` case; no tree-language type (GP 1).

New surface in `src/ToolUp.Platform.Client/Client/ClientHostBridge.fs` (module `ClientHostView`,
namespace `ToolUp.Platform`), beside `withElementView`:

- `withElementPanes : (Model -> (Msg -> unit) -> ClientHostCapabilities<Msg> -> ReactElement * ReactElement) -> ClientModule -> ClientModule`
  — the hosted peer of `ClientModule.withView`. The view returns a `(control, output)` pair of
  rendered trees, laid out as `PageContent.SplitPanel`. Both panes receive the **same** capability
  bag, built once from the module's dispatch.
- `withElementPages : (PageConfig * (Model -> (Msg -> unit) -> ClientHostCapabilities<Msg> -> PageContent)) list -> ClientModule -> ClientModule`
  — the hosted peer of `ClientModule.withPages`. Each page's view returns any `PageContent` case
  (`SplitPanel` / `Stacked` / `FullWidth` / `Dashboard` / `Custom`), so a hosted tree drives a
  multi-page module across every existing layout shape. All pages share one `Model` / dispatch, so
  every page's tree resolves its typed actions through **one** capability bag.

Projection-bound companions (Phase 264 read-side) in
`src/ToolUp.Platform.Client/Client/HostStateProjection.fs` (module `ClientBoundHostView`):

- `withBoundElementPanes` / `withBoundElementPages` — the same two shapes, but every region *also*
  receives the `HostBindingSources` projected from the current `Model`. The projection runs **once**
  per render and the same namespace is handed to every region — so a binding resolves identically in
  either pane / on every page.

Every existing `withView` / `withElementView` / `withFullWidthView` / `withPages` caller is untouched
(GP 11); a pipeline that never calls the new overloads pays nothing (GP 13).

## How to adopt (opt-in)

Split-pane hosted module:

```fsharp
ClientModule.create spec
|> ClientHostView.withElementPanes (fun model dispatch host ->
    MyTreeRuntime.render (controlTree model) host,      // control pane
    MyTreeRuntime.render (outputTree model) host)       // output pane
|> ClientModule.register
```

Multi-page hosted module (each page picks its own `PageContent` shape):

```fsharp
ClientModule.create spec
|> ClientHostView.withElementPages [
    overviewPage, (fun model dispatch host -> PageContent.FullWidth(MyTreeRuntime.render (overview model) host))
    detailPage,   (fun model dispatch host -> PageContent.SplitPanel(
                                                  MyTreeRuntime.render (controls model) host,
                                                  MyTreeRuntime.render (detail model) host)) ]
|> ClientModule.register
```

Bind host-projected state into every region (Phase 264):

```fsharp
ClientModule.create spec
|> ClientBoundHostView.withBoundElementPanes projection (fun model dispatch host sources ->
    MyTreeRuntime.render (controlTree model) host sources,
    MyTreeRuntime.render (outputTree model) host sources)
|> ClientModule.register
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
cd samples/MinimalClient && dotnet fable -o output   # client-tier bridge compiles under Fable
```

`HostedTreeLayoutTests` (`ToolUp.Platform.Tests/InProcess/HostedTreeLayoutTests.fs`) proves: the
overloads populate the right layout slot (`View` vs `PageViews`); a hosted tree renders into both
panes and across pages (via the toy tree's tier-neutral `lowerToHtml`); a `Navigate` / `Dispatch`
from every region routes to the shipped concretes; every `PageContent` case drives; the seam sources
carry no banned OSS vocabulary.

## Rollback

Remove the Phase 267 append block from `ClientHostBridge.fs` (`withElementPanes` / `withElementPages`)
and `HostStateProjection.fs` (`withBoundElementPanes` / `withBoundElementPages`); delete
`InProcess/HostedTreeLayoutTests.fs` + its `<Compile>` and `Program.fs` registration. No runtime
impact on any deployment that never hosted a multi-region tree.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in client-tree-hosting layout surface.
No current matrix consumer hosts a typed-tree UI; a deployment that composes neither overload is
byte-for-byte unchanged (GP 11/13).
