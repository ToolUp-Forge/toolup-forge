# Migration 299 — owning ComponentId on the hosting seam (identity bridge)

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

A hosted view tree (Phase 110 / 264) rendered without knowing **which** composition component it
belonged to, so an op or a telemetry event (Phase 297 usage export) could not resolve across the
composition ↔ view boundary — a node in a hosted view had no way back to the `ComponentId` (Phase 279)
of the module hosting it. This phase threads the owning `ComponentId` onto the hosting seam — the
forge half of the identity bridge.

New surface in `src/ToolUp.Platform.Client/Client/HostStateProjection.fs` (namespace `ToolUp.Platform`):

- `HostOwnership` — `{ OwningComponent: ComponentId option }`. `None` is an **untagged** host (the
  pre-299 behaviour).
- `HostOwnership.{untagged, ofComponent, owner, attribute}` — `attribute : HostOwnership -> 'a ->
  ComponentId option * 'a` pairs any value (an interaction event, a render fault, a resolved binding)
  with the owning id so a downstream telemetry export (Phase 297) or binding resolver (Phase 264) can
  correlate it across the composition ↔ view boundary.
- `ClientOwnedHostView.withOwnedBoundElementView` — `ClientBoundHostView.withBoundElementView`
  extended with the `HostOwnership`, so a hosted tree — and the events / bindings it raises — can
  attribute to the module that hosts it. Existing `withBoundElementView` callers are untouched; an
  untagged host attributes to `None` — byte-for-byte the pre-299 behaviour (GP 11).

The Phase 297 usage-export edge is served by the owning id being **available on the seam** (`attribute`
+ the `HostOwnership` handed to the view); Phase 297 consumes it when it lands. Wholly forge-public and
tree-language-free (GP 1).

## How to adopt (opt-in)

```fsharp
ClientModule.create spec
|> ClientOwnedHostView.withOwnedBoundElementView
       (HostOwnership.ofComponent (ComponentId.ofModule "sales"))
       (IHostStateProjection.ofFunc project)
       (fun model dispatch host sources ownership ->
           // Attribute an event / binding to the owning component:
           let owner, event = HostOwnership.attribute ownership someToyEvent
           MyTreeRuntime.render (page model) host sources ownership)
|> ClientModule.register
```

An existing `ClientBoundHostView.withBoundElementView` caller is unchanged (it is an untagged host,
`OwningComponent = None`).

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostingSeamComponentId"
cd samples/MinimalClient && dotnet fable -o output   # the client-tier seam compiles under Fable
```

## Rollback

Remove the Phase 299 append block from `HostStateProjection.fs`, delete
`InProcess/HostingSeamComponentIdTests.fs` + its `<Compile>` and `Program.fs` registration. No runtime
impact on any deployment that never tagged a host.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in identity-bridge seam. No current
matrix consumer hosts a typed-tree UI; an untagged host is byte-for-byte unchanged (GP 11/13).
