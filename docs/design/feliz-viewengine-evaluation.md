# Design evaluation — Feliz.ViewEngine for server-side layouts (Phase 92)

**Decision: ADOPT, as an opt-in.** `Feliz.ViewEngine` is referenced by
`ToolUp.PublicRendering` and surfaced through one adapter
(`FelizLayout.toGiraffe`) and one compose helper
(`PublicRenderingServerApp.withFelizLayout`). Giraffe.ViewEngine remains
the default layout DSL for every documented path; a deployment that
never calls `withFelizLayout` is behaviourally unchanged (GP 11) and
pays only the package's presence in the dependency graph.

## The question

SSR layouts are `PublicPage -> Giraffe.ViewEngine.XmlNode` functions;
Fable SPAs are authored in Feliz. The two DSLs are different, so a
hybrid deployment (SSR public front + Fable admin shell) cannot share
presentational component code between its server-rendered pages and its
SPA. Does adopting `Feliz.ViewEngine` — a server-side implementation of
the Feliz DSL that renders to HTML strings — give a real
author-once/render-twice story, and at what cost?

## Findings

### The shared-component story works, with a precise boundary

`Feliz.ViewEngine` re-implements the Feliz API surface (`Html.*`,
`prop.*`, `style.*`, `React.fragment`) against its own `ReactElement`
type and renders it to a string. Because the *source-level* DSL matches,
one `.fs` file compiles against both libraries behind a conditional
`open` — and `FABLE_COMPILER` is exactly the constant the SDK's
source-in-nupkg rule already sanctions:

```fsharp
#if FABLE_COMPILER
open Feliz            // client: Fable → React
#else
open Feliz.ViewEngine // server: → HTML string
#endif

let heroCard (title: string) (lede: string) =
    Html.section [
        prop.className "hero"
        prop.children [
            Html.h1 [ prop.style [ style.color "var(--bk-ink)" ]; prop.text title ]
            Html.p [ prop.text lede ]
        ]
    ]
```

The shareable subset is **pure presentational markup**: elements,
classes, inline styles (CSS variables render fine), aria props,
fragments, `dangerouslySetInnerHTML`. Out of bounds on the server:
hooks, `React.useState`, event handlers (`prop.onClick` compiles but is
meaningless in static HTML), `[<ReactComponent>]`, and any
`Feliz.UseElmish`-style stateful component. The worked example lives in
`samples/PublicSite/Layouts/FelizPageLayout.fs`; the prop-surface used
there (and in the adapter test pack) pins the subset we rely on.

Two caveats a consumer should know:

1. **Prop-surface drift.** Feliz proper (3.x) grows faster than
   Feliz.ViewEngine; a prop that exists client-side may not exist
   server-side. The failure mode is a compile error on the server
   branch — loud, not silent.
2. **Component identity.** A shared file is *source*-shared, not
   binary-shared. The server and client compile it separately; that is
   the same model as the SDK's `fable/` source-in-nupkg delivery.

### Render mechanics compose with the existing pipeline unchanged

Probed empirically (v1.0.3 on .NET 10): `Render.htmlView` emits the
element with **no doctype**; `Render.htmlDocument` prepends one. The
adapter therefore uses `htmlView` and wraps the string in Giraffe's
`rawText`, so the page handler's `RenderView.AsString.htmlDocument`
call adds the single `<!DOCTYPE html>` and the per-request
head-metadata injection (string-level, before `</head>`) works on the
result unchanged. No new render path, no registry change — a Feliz
layout is an ordinary entry in the same `Map<LayoutName, PublicPage ->
XmlNode>`.

### Dependency cost is small and bounded

- v1.0.3 (current at evaluation time), MIT-licensed, depends on
  **FSharp.Core only**, targets netstandard2.0 — runs on net10.0
  without warnings.
- Added to `ToolUp.PublicRendering` only — the platform core packages
  do not reference it. Consumers of other companions never see it.
- Third-party notices regenerated via the standard `ThirdPartyNotices`
  build target.

### Nullable-reference interaction

`ToolUp.PublicRendering` is server-only and follows the repo's default
(nullness unset), so no interaction arises there. A **shared**
component file compiled by a Fable client follows the existing rule —
"keep `<Nullable>enable</Nullable>` off Fable-touching projects" — which
governs the client project, not this package.

## Alternative considered: decline + document the glue

Declining the dependency and documenting "wrap
`Render.htmlView` output in `rawText` yourself" was viable (the
adapter is three lines), but it would push the doctype subtlety and
the registry idiom onto every consumer, and leave the shared-component
story untested in-tree. Owning the three lines plus a contract test
pack costs one tiny MIT dependency and makes the option discoverable
(`withFelizLayout` sits next to `withLayout` in the compose API).

## Decision summary

| Aspect | Outcome |
|---|---|
| Adopt? | Yes — opt-in via `withFelizLayout`; Giraffe stays the default (GP 2) |
| Where | `ToolUp.PublicRendering` (`Server/FelizLayoutAdapter.fs`) |
| Shared components | Source-shared presentational subset behind `#if FABLE_COMPILER` |
| Worked example | `samples/PublicSite/Layouts/FelizPageLayout.fs` |
| Tests | `FelizLayoutAdapterTests` (single doctype, registry round-trip, prop subset) |
| Revisit when | Feliz.ViewEngine stalls behind a Feliz major rev that breaks the shared subset |
