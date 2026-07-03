# Migration 298 — live preview of an unreduced composition's view subtrees

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

A loop-speed unlock for the typed-tree-view authoring path: render the hosted-tree view subtrees of a
**partial / in-progress composition without a built solution** — the visible surface of an external
tool's app *before* it is reduced to a compiled forge app — turning an authoring iteration from a
`dotnet build` (minutes) into a re-render (milliseconds). Builds on Phase 264 (the `HostBindingSources`
the preview resolves against) + Phase 267 (`PageContent` multi-region hosting).

New surface in `src/ToolUp.Platform.Client/Client/UnreducedViewPreview.fs` (namespace `ToolUp.Platform`):

- `UnreducedViewSubtree = { Label; RequiredBindings: string list; Render: HostBindingSources -> ReactElement }`
  — one view subtree of a partial composition, plus the host-projected binding keys it needs.
- `PreviewOutcome = Rendered | Placeholder of unresolved: string list` — the tier-neutral preview
  decision for a subtree.
- `UnreducedViewPreview.{unresolvedBindings, outcome, outcomes}` — pure functions computing whether a
  subtree's required bindings all resolve (`Rendered`) or which are missing (`Placeholder`). Because
  they are pure functions of `(subtrees, sources)`, re-evaluating after an edit needs **no rebuild** —
  the loop-speed property.
- `UnreducedViewPreview.render : UnreducedViewSubtree list -> HostBindingSources -> ReactElement` —
  renders the whole partial composition's visible surface live (CSR). A subtree whose required binding
  is unresolved (or whose `Render` throws) **degrades to a labelled placeholder, never an exception** —
  a composition is in progress by definition.

Serves the typed-tree-view path only: a bare-Feliz / non-hosted consumer uses the standard React/Vite
dev loop and needs none of this (GP 13). Wholly forge-public + tree-language-neutral (GP 1) — a subtree
is a label + required binding keys + a render thunk; no tree type appears.

## How to adopt (opt-in)

```fsharp
let subtrees = [
    { Label = "counter"; RequiredBindings = [ "count" ];  Render = fun s -> MyTreeRuntime.render (counter ()) s }
    { Label = "totals";  RequiredBindings = [ "total" ];  Render = fun s -> MyTreeRuntime.render (totals ())  s }
]

// on every tree edit — re-render, no rebuild; an unresolved subtree shows a placeholder:
let preview = UnreducedViewPreview.render subtrees currentProjection
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
cd samples/MinimalClient && dotnet fable -o output   # client-tier preview compiles under Fable
```

`UnreducedViewPreviewTests` proves: a subtree whose bindings resolve renders; an unresolved binding
degrades to a `Placeholder` (pure, never throws); an edited subtree re-previews with no rebuild; a
partial composition previews per-subtree; the preview binding namespace is the Phase 264 read-side (the
toy resolves a `Bind` against the same projection); the seam carries no banned OSS vocabulary.

## Rollback

Delete `UnreducedViewPreview.fs` + its `<Compile>` entry; delete `InProcess/UnreducedViewPreviewTests.fs`
+ its `<Compile>` and `Program.fs` registration. No runtime impact on any deployment that never
previewed an unreduced composition.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a pre-reduction authoring-loop surface for the
typed-tree-view path. No current matrix consumer hosts a typed-tree UI; a bare-Feliz consumer is
unaffected and pays nothing (GP 11/13).
