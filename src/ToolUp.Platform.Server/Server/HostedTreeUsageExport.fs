// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Generic

// ─── Phase 297 — ComponentId-keyed hosted-tree usage telemetry export ──
//
// Phase 268 captures hosted-tree render/binding FAULTS; Phase 283 correlates
// telemetry by the stable Phase 279 `ComponentId`. This phase widens the
// captured signal from faults to USAGE / interaction — action-dispatch reach,
// surface visibility, capability invocation — keyed by `ComponentId`, and
// exposes it in a consumable, scope-isolated form so an external authoring
// tool can attribute *runtime behaviour* back to the composition node that
// produced it (the forge half of the runtime→authoring feedback edge).
//
// **Generic observability substrate (GP 1).** No vendor and no tree-language
// type: a usage event is a `ComponentId` + a three-case kind + an opaque,
// host-owned name. The export powers analytics / A-B / ops dashboards without
// any of them depending on a concrete renderer.
//
// **Scope-isolated by construction (GP 4).** Every event is recorded under a
// `scopeId`; a `Snapshot` resolves ONLY that scope's partition, so one
// scope's usage can never appear in another's export — the isolation is
// carried by the seam shape, not a remembered filter (the same posture
// `ITelemetrySink` / the live-session host take).
//
// **Zero cost when unused (GP 13).** The default `NoOpHostedTreeUsageExport`
// records nothing and snapshots empty; a pipeline that composes no export
// pays nothing and is byte-for-byte unchanged (GP 11).
//
// **Portability (GP 12).** `Record` is `unit`-returning (write-only hot-path,
// the documented `IMetricsSink` exception — a usage tick never blocks the
// dispatch that raised it); `Snapshot` is `Async` (rule 2 — a distributed
// export polls over the network). Identity by value (`ComponentId` / scopeId
// strings). Handlers hold no per-call state between records (rule 4). The
// in-memory impl is the dev / single-instance reference; a distributed export
// is a companion.

/// The kind of hosted-tree usage / interaction event — the three runtime
/// signals a hosted tree raises beyond a render fault.
[<RequireQualifiedAccess>]
type HostedTreeUsageEventKind =
    /// A typed action was dispatched from the hosted tree (interaction reach).
    | ActionDispatched
    /// A hosted surface / region became visible (impression).
    | SurfaceVisible
    /// A named host capability was invoked (Phase 266 `Invoke` reach).
    | CapabilityInvoked

/// A single usage/interaction event, attributed to the composition node
/// (`ComponentId`) that produced it. `Name` is the opaque, host-owned name of
/// the action / surface / capability (forge never interprets it); `None` when
/// the raiser names nothing.
type HostedTreeUsageEvent = {
    Component: ComponentId
    Kind: HostedTreeUsageEventKind
    Name: string option
}

[<RequireQualifiedAccess>]
module HostedTreeUsageEvent =
    /// An action-dispatch usage event for `componentId` naming `action`.
    let actionDispatched (componentId: ComponentId) (action: string) : HostedTreeUsageEvent = {
        Component = componentId
        Kind = HostedTreeUsageEventKind.ActionDispatched
        Name = Some action
    }

    /// A surface-visibility usage event for `componentId` naming `surface`.
    let surfaceVisible (componentId: ComponentId) (surface: string) : HostedTreeUsageEvent = {
        Component = componentId
        Kind = HostedTreeUsageEventKind.SurfaceVisible
        Name = Some surface
    }

    /// A capability-invocation usage event for `componentId` naming `capability`.
    let capabilityInvoked (componentId: ComponentId) (capability: string) : HostedTreeUsageEvent = {
        Component = componentId
        Kind = HostedTreeUsageEventKind.CapabilityInvoked
        Name = Some capability
    }

/// Per-component usage tallies within one scope — the aggregated shape an
/// external tool consumes.
type HostedTreeUsageCounts = {
    ActionDispatches: int
    SurfaceVisibilities: int
    CapabilityInvocations: int
}

[<RequireQualifiedAccess>]
module HostedTreeUsageCounts =
    /// The zero tally.
    let empty: HostedTreeUsageCounts = {
        ActionDispatches = 0
        SurfaceVisibilities = 0
        CapabilityInvocations = 0
    }

    /// Total events across all kinds.
    let total (c: HostedTreeUsageCounts) : int =
        c.ActionDispatches + c.SurfaceVisibilities + c.CapabilityInvocations

    /// Increment the tally for one event kind.
    let bump (kind: HostedTreeUsageEventKind) (c: HostedTreeUsageCounts) : HostedTreeUsageCounts =
        match kind with
        | HostedTreeUsageEventKind.ActionDispatched -> {
            c with
                ActionDispatches = c.ActionDispatches + 1
          }
        | HostedTreeUsageEventKind.SurfaceVisible -> {
            c with
                SurfaceVisibilities = c.SurfaceVisibilities + 1
          }
        | HostedTreeUsageEventKind.CapabilityInvoked -> {
            c with
                CapabilityInvocations = c.CapabilityInvocations + 1
          }

/// A scope-isolated, `ComponentId`-keyed snapshot of hosted-tree usage — the
/// consumable feed an external authoring / analytics tool polls. Carries only
/// the requested scope's usage (GP 4).
type HostedTreeUsageSnapshot = {
    ScopeId: string
    ByComponent: Map<ComponentId, HostedTreeUsageCounts>
}

[<RequireQualifiedAccess>]
module HostedTreeUsageSnapshot =
    /// The empty snapshot for a scope with no recorded usage.
    let empty (scopeId: string) : HostedTreeUsageSnapshot = {
        ScopeId = scopeId
        ByComponent = Map.empty
    }

/// The hosted-tree usage export seam. `Record` is the write-only hot-path
/// emission (sync, the `IMetricsSink` exception); `Snapshot` is the async
/// consumable read (a distributed export polls over the network). Default:
/// `NoOpHostedTreeUsageExport`.
type IHostedTreeUsageExport =
    /// Record a usage event under `scopeId`. Best-effort, write-only, never
    /// throws across the boundary; a hosted tree raises usage through it and
    /// continues.
    abstract Record: scopeId: string -> event: HostedTreeUsageEvent -> unit

    /// The scope-isolated, id-keyed usage snapshot for `scopeId` — only that
    /// scope's usage (GP 4). Async so a distributed export can poll a store.
    abstract Snapshot: scopeId: string -> Async<HostedTreeUsageSnapshot>

/// The default no-op export — records nothing, snapshots empty. Composed when
/// a deployment exports no hosted-tree usage, so emission sites are free at
/// runtime and the pipeline is byte-for-byte unchanged (GP 11 / GP 13).
type NoOpHostedTreeUsageExport() =
    interface IHostedTreeUsageExport with
        member _.Record (_scopeId: string) (_event: HostedTreeUsageEvent) = ()
        member _.Snapshot(scopeId: string) = async { return HostedTreeUsageSnapshot.empty scopeId }

/// The dev / single-instance in-memory reference export. Scope-isolated by
/// construction: events partition by `scopeId`, and a `Snapshot` reads ONLY
/// its scope's partition — a snapshot for scope A can never surface scope B's
/// usage (GP 4). A distributed, multi-instance export is a companion (a
/// shared store behind the same seam); this impl is marked dev / single-
/// instance because it holds process-local state.
type InMemoryHostedTreeUsageExport() =
    // scopeId → (ComponentId → tallies). A plain dictionary guarded by a lock
    // (usage recording is a concurrent hot path; a distributed impl owns its
    // own store).
    let byScope = Dictionary<string, Dictionary<ComponentId, HostedTreeUsageCounts>>()
    let gate = obj ()

    interface IHostedTreeUsageExport with
        member _.Record (scopeId: string) (event: HostedTreeUsageEvent) =
            lock gate (fun () ->
                let scopeMap =
                    match byScope.TryGetValue scopeId with
                    | true, m -> m
                    | false, _ ->
                        let m = Dictionary<ComponentId, HostedTreeUsageCounts>()
                        byScope[scopeId] <- m
                        m

                let current =
                    match scopeMap.TryGetValue event.Component with
                    | true, c -> c
                    | false, _ -> HostedTreeUsageCounts.empty

                scopeMap[event.Component] <- HostedTreeUsageCounts.bump event.Kind current)

        member _.Snapshot(scopeId: string) = async {
            return
                lock gate (fun () ->
                    match byScope.TryGetValue scopeId with
                    | true, m -> {
                        ScopeId = scopeId
                        ByComponent = m |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                      }
                    | false, _ -> HostedTreeUsageSnapshot.empty scopeId)
        }