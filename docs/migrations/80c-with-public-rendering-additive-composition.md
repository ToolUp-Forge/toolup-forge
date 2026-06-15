# Migration: Phase 80c — `withPublicRendering` additive composition

**Shipped:** 2026-06-05.

## What changes

`ToolUp.PublicRendering` gains additive-composition surface mirroring the [Phase 1h](01h-combinable-composition-roots.md) `withForms` / `withAI` pattern. Before this phase, `PublicRenderingServerApp.run` and `ServerApp.run` (or `AIServerApp.run`, etc.) were mutually-exclusive termini — an app could not host a public-rendering SSR surface *alongside* a base `ServerApp` carrying its own modules + Fable client. With Phase 80c, both compose on one pipeline.

**Three new public surfaces, all additive (no breaking change):**

- `PublicRenderingCompose.PublicRenderingServerApp.createFrom : ServerApp -> PublicRenderingServerApp` — lifts a base `ServerApp` into a fresh `PublicRenderingServerApp` whose `Base` is the input.
- `PublicRenderingCompose.PublicRenderingServerApp.composePublicRendering : PublicRenderingServerApp -> ServerApp` — produces the composed `ServerApp` without driving it. `[<EditorBrowsable(Never)>]`; consumers should reach for the `withPublicRendering` extension below.
- `PublicRenderingCompose.withPublicRendering : (PublicRenderingServerApp -> PublicRenderingServerApp) -> ServerApp -> ServerApp` — the additive extension. Mirrors `FormsCompose.withForms` shape exactly.

`PublicRenderingServerApp.run` is now defined as `composePublicRendering >> ServerApp.run` and continues to work byte-for-byte unchanged for every existing single-superset consumer.

## Diff to apply (consumer-side)

### Before (single-superset)

```fsharp
open ToolUp.PublicRendering

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig myConfig
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withLayout (LayoutName "article") articleLayout
|> PublicRenderingServerApp.run
```

### After (hybrid — PublicRendering + a domain module on one pipeline)

```fsharp
open ToolUp.Platform
open ToolUp.PublicRendering

ServerApp.empty
|> ServerApp.withConfig myConfig
|> ServerApp.withStorage storage
|> ServerApp.addModule myAdminModule
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withLayout (LayoutName "article") articleLayout)
|> ServerApp.run
```

Existing single-superset apps need **no changes** — `PublicRenderingServerApp.run` still works.

### Composing with Forms or AI on the same pipeline

The Phase 1h extensions are interleavable in any order:

```fsharp
ServerApp.empty
|> ServerApp.withConfig myConfig
|> FormsCompose.withForms (fun f -> f |> FormsServerApp.withFormSchema mySchema)
|> AICompose.withAI factory providerProfile (fun ai -> ai |> AIServerApp.withAIConfig assistant)
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withFeed myAtomFeed)
|> ServerApp.run
```

Each companion appends its handlers to the route chain; conflict diagnostics fire if any companion is composed twice.

## Strip-imports guarantee

When `ServerConfig.PublicRendering = NoPublicRendering` (the default), `composePublicRendering` returns `app.Base` unchanged — **zero DI registrations, zero handlers, zero hosted services**. The companion marker is NOT appended in this case, so a later `withPublicRendering` on the same pipeline composes freely (a deployment can opt into PublicRendering by flipping the `ServerConfig` field at startup without changing its composition root).

## Conflict diagnostics

A second `withPublicRendering` on the same pipeline trips `ServerApp.ensureCompanionNotAlreadyComposed`, which raises with a clear diagnostic naming the companion + resolution paths. This pre-empts the cascading duplicate-route-mount / duplicate-entity-registration failures that the second composition would otherwise surface deep inside `compose` or at first request. The Phase 1h `ComposedCompanions` marker on `ServerApp` is the underlying mechanism — Phase 80c opts PublicRendering into the existing convention.

## Verification

- `dotnet build src/ToolUp.PublicRendering/ToolUp.PublicRendering.fsproj` clean.
- `dotnet build ToolUp.Forge.sln` clean across the full SDK.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 1,829 tests passing including the four new Phase 80c composition tests in `InProcess/PublicRenderingTests.fs`:
  - `createFrom lifts the base ServerApp into a PublicRenderingServerApp`
  - `composePublicRendering with NoPublicRendering is a strip-imports pass-through (no marker, double-compose safe)`
  - `composePublicRendering with EnabledPublicRendering appends the ToolUp.PublicRendering companion marker`
  - `second withPublicRendering on the same pipeline trips ensureCompanionNotAlreadyComposed with a clear diagnostic`

## Rollback

Single file. Revert `src/ToolUp.PublicRendering/Server/PublicRenderingCompose.fs` to the pre-Phase-80c state — the new functions disappear and `run` collapses back to its prior in-line implementation. No consumer code touched (every change is additive); no migration required to roll back.
