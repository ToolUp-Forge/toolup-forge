# Migration 268 — hosted-tree render-failure + binding-resolution telemetry sink

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

The Phase 110 host-hosting seam lets an external typed-tree UI render into the SDK shell, but when a
node fails to render or a data binding fails to resolve, the symptom was a **console warning nobody
reads** — the "silent runtime behaviour with no observability" smell. This phase ships a vendor-neutral
contract that makes those faults observable, feeding forge's existing observability spine (structured
logging / metrics) without naming a tree language. No-op by default (GP 13), so a host that wants the
old silence keeps it by explicit choice.

New contract in `src/ToolUp.Platform.Core/Shared/Types/HostRenderFault.fs` (namespace `ToolUp.Platform`):

- `HostRenderFaultKind` — `RenderFault | BindingResolutionFault`.
- `HostRenderFault` — `{ NodeId: string; Kind: HostRenderFaultKind; Message: string; Binding: string option }`.
  `NodeId` is an opaque string the host owns; no tree-language payload type appears (GP 1).
- `HostRenderFault.{render, bindingResolution, describe}` — constructors + a stable, greppable one-line
  description (what a forwarding sink logs and the Phase 273 SSR fallback renders).
- `IHostRenderTelemetrySink` — `Capture : HostRenderFault -> unit`. `unit`-returning (not `Async`) by
  the same hot-path exception `IMetricsSink` takes — write-only, fire-and-forget, never throws across
  the boundary.
- `NoOpHostRenderTelemetrySink` — the true-no-op default (no allocation beyond the object, no network,
  no log).

The contract lives in **Core** (not the Client tier) deliberately: render faults happen on the client,
but the SSR path (Phase 273's `HostRenderBoundary`, in `ToolUp.PublicRendering` which references Core
not Client) reports through the **same** sink. This mirrors `ITelemetrySink` / `NoOpTelemetrySink`
exactly — interface + no-op default in Core, forwarding implementations in the tier that owns the
transport.

Client-tier forwarding default in `src/ToolUp.Platform.Client/Client/HostRenderTelemetry.fs`
(module `ToolUp.Platform.HostRenderTelemetry`):

- `forwardingToLogger : ILogger -> IHostRenderTelemetrySink` — writes each fault to `logger.Warn`.
- `defaultSink : IHostRenderTelemetrySink` — the forwarding default over the console logger
  (`client.host-render` category).
- `onMismatch : IHostRenderTelemetrySink -> string -> (HostCapabilityMismatch -> unit)` — bridges a
  Phase 270 capability-negotiation mismatch onto the sink as a `RenderFault` (the concrete wiring the
  Phase 270 `withNegotiatedElementView` `onMismatch` hook documents).
- `CountingHostRenderTelemetrySink(inner)` — a counting decorator (`.Count`) the client
  boot-degradation / health surface reads so a render-fault spike is visible, not just logged
  (Phase 121 precedent).

## How to adopt (opt-in)

```fsharp
// Wire the forwarding default (or a custom sink) where a hosted tree reports faults:
let sink = HostRenderTelemetry.defaultSink

// e.g. bridge a Phase 270 capability mismatch onto it at mount:
ClientModule.create spec
|> ClientHostNegotiatedView.withNegotiatedElementView required host
       (HostRenderTelemetry.onMismatch sink "my-tree-root")
       (fun model dispatch host -> MyTreeRuntime.render (view model) host)
|> ClientModule.register
```

A host that wires no sink keeps the Core `NoOpHostRenderTelemetrySink` and pays nothing (GP 13); a
pipeline that never constructs a sink is byte-for-byte unchanged vs a pre-268 build (GP 11).

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostRenderTelemetry"
cd samples/MinimalClient && dotnet fable -o output --noCache   # Client-tier sink compiles under Fable
```

## Rollback

Delete `Shared/Types/HostRenderFault.fs` + `Client/HostRenderTelemetry.fs` + their `<Compile>` entries,
delete `InProcess/HostRenderTelemetryTests.fs` + its `<Compile>` and `Program.fs` registration. No
runtime impact on any deployment that never wired a sink.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in hosted-tree observability contract. No
current matrix consumer hosts a typed-tree UI; a deployment that wires no sink is byte-for-byte
unchanged (GP 11/13).
