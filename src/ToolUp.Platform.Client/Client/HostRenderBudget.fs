// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 278 — hosted-tree render-cost budget gate ───────────────────
//
// An AI-emitted (or runaway server-driven) hosted tree can blow up in node
// count, depth, or render time; Phase 192 / 213 budget cold-start +
// web-vitals but neither sees TREE SHAPE. This file ships a neutral
// `HostRenderBudget` — declared limits (max nodes / max depth / render-time
// budget) that WARN at runtime (through the Phase 268
// `IHostRenderTelemetrySink`) and GATE in CI against fixtures — so an
// over-budget tree is a SIGNAL, not silent jank.
//
// **Neutral (GP 1).** The budget is a record of numeric limits over a
// generic node-count / depth / time MEASURE; no tree-language type appears.
// `measureTree` is generic over any children-function, so a stranger tree
// language (the Phase 202 `ToyNode` witness included) is measurable.
//
// **A budget is a signal, not a hard kill.** `reportBreaches` emits each
// breach as a Phase 268 `HostRenderFault` and RETURNS — non-fatal by
// default. A consumer that wants a hard failure opts into `enforce`, which
// raises after reporting. Not configured = no measurement, byte-for-byte
// unchanged (GP 11), zero cost (GP 13).

/// Declared render-cost limits for a hosted tree. Each dimension is
/// optional — `None` leaves that dimension unbounded. An all-`None` budget
/// (`HostRenderBudget.unlimited`) is "not configured": `evaluate` performs
/// no measurement and always returns `WithinBudget` (GP 13).
type HostRenderBudget = {
    /// Maximum node count in the rendered tree.
    MaxNodes: int option
    /// Maximum tree depth (root = depth 1).
    MaxDepth: int option
    /// Maximum render time in milliseconds (compared against a measured
    /// render time the host supplies; `None` on either side skips it).
    MaxRenderMillis: float option
}

/// A measurement of one hosted render, compared against a `HostRenderBudget`.
/// The host supplies it (from `measureTree` + an optional render timing).
type HostRenderMeasure = {
    NodeCount: int
    Depth: int
    /// Measured render time, if the host timed the render; `None` skips the
    /// render-time check.
    RenderMillis: float option
}

/// One budget breach — which dimension exceeded its limit, with the actual
/// value and the budget it broke.
type HostBudgetBreach =
    | NodesExceeded of actual: int * budget: int
    | DepthExceeded of actual: int * budget: int
    | RenderTimeExceeded of actualMs: float * budgetMs: float

/// The outcome of a budget evaluation.
type HostRenderBudgetResult =
    | WithinBudget
    | OverBudget of HostBudgetBreach list

[<RequireQualifiedAccess>]
module HostRenderBudget =

    /// No limits — "not configured". `evaluate` against it performs no
    /// measurement (GP 13).
    let unlimited: HostRenderBudget = {
        MaxNodes = None
        MaxDepth = None
        MaxRenderMillis = None
    }

    /// True when at least one dimension is bounded.
    let isConfigured (b: HostRenderBudget) : bool =
        b.MaxNodes.IsSome || b.MaxDepth.IsSome || b.MaxRenderMillis.IsSome

    /// A budget bounding node count + depth (render time unbounded).
    let ofShape (maxNodes: int) (maxDepth: int) : HostRenderBudget = {
        MaxNodes = Some maxNodes
        MaxDepth = Some maxDepth
        MaxRenderMillis = None
    }

    /// Count nodes + measure max depth of ANY tree, given a children
    /// function. Neutral over the tree language (GP 1) — the host passes its
    /// own tree's `children`. Root is depth 1. Returns `(nodeCount, depth)`.
    let measureTree (children: 'n -> 'n list) (root: 'n) : int * int =
        let rec go depth node =
            let mutable count = 1
            let mutable maxDepth = depth

            for child in children node do
                let c, d = go (depth + 1) child
                count <- count + c
                maxDepth <- max maxDepth d

            count, maxDepth

        go 1 root

    /// Build a measure from a tree (via `measureTree`) plus an optional
    /// measured render time.
    let measureOf (children: 'n -> 'n list) (renderMillis: float option) (root: 'n) : HostRenderMeasure =
        let nodes, depth = measureTree children root

        {
            NodeCount = nodes
            Depth = depth
            RenderMillis = renderMillis
        }

    /// Evaluate a measure against a budget. An unconfigured budget performs
    /// NO measurement and returns `WithinBudget` (GP 13); otherwise every
    /// exceeded dimension is named.
    let evaluate (budget: HostRenderBudget) (measure: HostRenderMeasure) : HostRenderBudgetResult =
        if not (isConfigured budget) then
            WithinBudget
        else
            let breaches = [
                match budget.MaxNodes with
                | Some m when measure.NodeCount > m -> NodesExceeded(measure.NodeCount, m)
                | _ -> ()

                match budget.MaxDepth with
                | Some m when measure.Depth > m -> DepthExceeded(measure.Depth, m)
                | _ -> ()

                match budget.MaxRenderMillis, measure.RenderMillis with
                | Some b, Some a when a > b -> RenderTimeExceeded(a, b)
                | _ -> ()
            ]

            if List.isEmpty breaches then
                WithinBudget
            else
                OverBudget breaches

    /// A stable, greppable one-line description of a breach — what the
    /// Phase 268 sink records and the CI gate prints.
    let describeBreach (breach: HostBudgetBreach) : string =
        match breach with
        | NodesExceeded(actual, budget) -> sprintf "hosted tree node count %d exceeds budget %d" actual budget
        | DepthExceeded(actual, budget) -> sprintf "hosted tree depth %d exceeds budget %d" actual budget
        | RenderTimeExceeded(actual, budget) -> sprintf "hosted tree render time %gms exceeds budget %gms" actual budget

    /// Report every breach in a result through the Phase 268 render-fault
    /// sink as a `RenderFault` against `nodeId` — NON-FATAL: a budget is a
    /// signal, so this returns whether it was over budget without raising. A
    /// `WithinBudget` result emits nothing (GP 13).
    let reportBreaches (sink: IHostRenderTelemetrySink) (nodeId: string) (result: HostRenderBudgetResult) : bool =
        match result with
        | WithinBudget -> false
        | OverBudget breaches ->
            for breach in breaches do
                sink.Capture(HostRenderFault.render nodeId (describeBreach breach))

            true

    /// Hard-fail on an over-budget result (the opt-in strict mode) — reports
    /// each breach through the sink, then raises. Use only when a deployment
    /// wants an over-budget tree to be a build/runtime failure rather than a
    /// warning.
    let enforce (sink: IHostRenderTelemetrySink) (nodeId: string) (result: HostRenderBudgetResult) : unit =
        if reportBreaches sink nodeId result then
            match result with
            | OverBudget breaches ->
                let detail = breaches |> List.map describeBreach |> String.concat "; "
                failwithf "hosted render budget exceeded at node '%s': %s" nodeId detail
            | WithinBudget -> ()