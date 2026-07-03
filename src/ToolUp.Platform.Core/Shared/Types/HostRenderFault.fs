// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 268 — neutral hosted-tree render-fault contract ─────────────
//
// The Phase 110 host-hosting seam lets an external typed-tree UI render
// into the SDK shell, but when a node fails to render or a data binding
// fails to resolve, the symptom today is a console warning nobody reads —
// the "silent runtime behaviour with no observability" smell. This file
// ships the vendor-neutral CONTRACT that makes those faults observable:
// a `HostRenderFault` value, an `IHostRenderTelemetrySink` that captures
// it, and a true-no-op default so a pipeline that wants the old silence
// keeps it by explicit choice (GP 13).
//
// The contract lives in Core (not the Client tier) deliberately: render
// faults happen on the client, but the SSR path (Phase 273's
// `HostRenderBoundary`) reports SERVER-side faults through the SAME sink,
// and `ToolUp.PublicRendering` references Core, not Client. Placing the
// contract here is the exact shape `ITelemetrySink` / `NoOpTelemetrySink`
// already use — the interface + no-op default in Core, the forwarding
// implementations in the tier that owns the transport (client console
// logger, server structured logger).
//
// **Renderer-neutral by construction (GP 1).** `NodeId` is an opaque
// string the host owns; the fault kind is a two-case DU; no tree-language
// payload type appears. Grep-guarded: the contract carries no
// banned-vocabulary token.
//
// **Six portability rules (GP 12).** Identity by value (`NodeId` string,
// a record of values); no live handles; `Capture` is a point sink call
// with no ordering/timing promise; a distributed sink wraps its own
// batching/retry around `Capture`. `Capture` is `unit`-returning (not
// `Async`) by the SAME documented exception `IMetricsSink` takes — it is
// a hot-path, write-only, fire-and-forget observability emission with
// nothing to await; an implementation that ships faults over a transport
// owns that async internally and never blocks the render path.

/// The class of fault a hosted tree hit. A neutral two-case DU — a node
/// either failed to render or a binding failed to resolve.
[<RequireQualifiedAccess>]
type HostRenderFaultKind =
    /// A node/subtree failed to render (a renderer threw, an unknown node
    /// kind, a malformed subtree).
    | RenderFault
    /// A data binding failed to resolve (an unknown binding source, a
    /// missing key, a shape mismatch).
    | BindingResolutionFault

/// A neutral, renderer-agnostic description of a single hosted-tree
/// render / binding-resolution fault. Identity-by-value (GP 12 rule 1):
/// `NodeId` is an opaque string the host owns; no tree-language payload
/// type appears. Carries the fault kind, a human-readable message, and —
/// for a binding fault — the optional binding/slot name that failed.
type HostRenderFault = {
    /// Opaque id of the node/subtree that faulted. The host owns the id
    /// space; forge never interprets it beyond correlation / reporting.
    NodeId: string
    /// Whether the node failed to render or a binding failed to resolve.
    Kind: HostRenderFaultKind
    /// Human-readable description of what went wrong.
    Message: string
    /// For a `BindingResolutionFault`, the binding / slot name that failed
    /// to resolve; `None` for a render fault (or an anonymous binding).
    Binding: string option
}

[<RequireQualifiedAccess>]
module HostRenderFault =

    /// A render fault for `nodeId` carrying `message`.
    let render (nodeId: string) (message: string) : HostRenderFault = {
        NodeId = nodeId
        Kind = HostRenderFaultKind.RenderFault
        Message = message
        Binding = None
    }

    /// A binding-resolution fault for `nodeId` naming the `binding` that
    /// failed, carrying `message`.
    let bindingResolution (nodeId: string) (binding: string) (message: string) : HostRenderFault = {
        NodeId = nodeId
        Kind = HostRenderFaultKind.BindingResolutionFault
        Message = message
        Binding = Some binding
    }

    /// A stable, greppable one-line description of a fault — what a
    /// forwarding sink logs and what the Phase 273 SSR fallback fragment
    /// renders. Names the node id and, for a binding fault, the binding.
    let describe (fault: HostRenderFault) : string =
        match fault.Kind with
        | HostRenderFaultKind.RenderFault -> sprintf "render fault at node '%s': %s" fault.NodeId fault.Message
        | HostRenderFaultKind.BindingResolutionFault ->
            let binding = fault.Binding |> Option.defaultValue "(unnamed)"
            sprintf "binding-resolution fault at node '%s' (binding '%s'): %s" fault.NodeId binding fault.Message

/// A vendor-neutral sink for hosted-tree render / binding-resolution
/// faults. `Capture` is fire-and-forget (`unit`-returning by the same
/// hot-path exception `IMetricsSink` takes) — a hosted runtime reports a
/// fault through it and continues; the sink MUST NOT throw across the
/// boundary (a telemetry failure never breaks the render that emitted it).
/// Default: `NoOpHostRenderTelemetrySink` (a true no-op).
type IHostRenderTelemetrySink =
    /// Record a hosted-tree fault. Best-effort — never throws across the
    /// boundary; a sink that ships faults over a transport owns any async
    /// / batching / retry internally.
    abstract Capture: fault: HostRenderFault -> unit

/// The default no-op sink — a true no-op (no allocation beyond the
/// object, no network, no log). Composed when a host wires no telemetry,
/// so the old silence is kept by EXPLICIT choice and emission sites are
/// free at runtime (GP 13).
type NoOpHostRenderTelemetrySink() =
    interface IHostRenderTelemetrySink with
        member _.Capture(_fault: HostRenderFault) = ()