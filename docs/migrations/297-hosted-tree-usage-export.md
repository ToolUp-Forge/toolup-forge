# Migration 297 — ComponentId-keyed hosted-tree usage telemetry export

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

Phase 268 captures hosted-tree render/binding **faults**; Phase 283 correlates telemetry by the stable
Phase 279 `ComponentId`. This phase widens the captured signal from faults to **usage / interaction** —
action-dispatch reach, surface visibility, capability invocation — keyed by `ComponentId`, and exposes
it in a consumable, scope-isolated form so an external authoring tool can attribute *runtime behaviour*
back to the composition node that produced it (the forge half of the runtime→authoring feedback edge).

New surface in `src/ToolUp.Platform.Server/Server/HostedTreeUsageExport.fs` (namespace `ToolUp.Platform`):

- `HostedTreeUsageEventKind` — `ActionDispatched | SurfaceVisible | CapabilityInvoked`.
- `HostedTreeUsageEvent` — `{ Component: ComponentId; Kind: HostedTreeUsageEventKind; Name: string option }`
  + `HostedTreeUsageEvent.{actionDispatched, surfaceVisible, capabilityInvoked}` constructors. `Name` is
  the opaque, host-owned action/surface/capability name.
- `HostedTreeUsageCounts` — per-component `{ ActionDispatches; SurfaceVisibilities; CapabilityInvocations }`
  + `empty` / `total` / `bump`.
- `HostedTreeUsageSnapshot` — `{ ScopeId: string; ByComponent: Map<ComponentId, HostedTreeUsageCounts> }`
  — the consumable, id-keyed feed for one scope.
- `IHostedTreeUsageExport` — `Record : scopeId -> event -> unit` (write-only hot-path, sync, the
  documented `IMetricsSink` exception — a usage tick never blocks the dispatch that raised it) +
  `Snapshot : scopeId -> Async<HostedTreeUsageSnapshot>` (async read — a distributed export polls a
  store).
- `NoOpHostedTreeUsageExport` — the default: records nothing, snapshots empty (GP 13).
- `InMemoryHostedTreeUsageExport` — the dev / single-instance reference. **Scope-isolated by
  construction (GP 4)**: events partition by `scopeId`, and a `Snapshot` reads only its scope's
  partition — a snapshot for scope A can never surface scope B's usage.

**Generic observability substrate (GP 1)** — no vendor and no tree-language type; usage export is
generic (analytics / A-B / ops dashboards). A deployment that composes no export pays nothing (GP 13)
and is byte-for-byte unchanged (GP 11). Portability (GP 12): identity by value, sync write / async read,
stateless handlers; the in-memory impl is dev/single-instance, a distributed export is a companion.

## How to adopt (opt-in)

```fsharp
let usage : IHostedTreeUsageExport = InMemoryHostedTreeUsageExport()   // or a distributed companion

// A hosted tree raises usage keyed by the owning ComponentId (Phase 279/283):
usage.Record scopeId (HostedTreeUsageEvent.actionDispatched componentId "checkout")

// An external tool polls a scope-isolated, id-keyed snapshot:
let! snap = usage.Snapshot scopeId
// snap.ByComponent : Map<ComponentId, HostedTreeUsageCounts>
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostedTreeUsageExport"
```

## Rollback

Delete `Server/HostedTreeUsageExport.fs` + its `<Compile>` entry, delete
`InProcess/HostedTreeUsageExportTests.fs` + its `<Compile>` and `Program.fs` registration. No runtime
impact on any deployment that never composed an export.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in hosted-tree usage-export substrate. No
current matrix consumer hosts a typed-tree UI; a deployment that composes no export is byte-for-byte
unchanged (GP 11/13).
