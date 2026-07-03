// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz

// ─── Phase 298 — live preview of an unreduced composition's view subtrees ──
//
// The loop-speed unlock: render the hosted-tree view subtrees of an
// external tool's IN-PROGRESS app *before* it is reduced to a compiled
// forge app — turning an authoring iteration from a `dotnet build`
// (minutes) into a re-render (milliseconds). Builds on Phase 264 (the
// `HostBindingSources` the preview resolves against) + Phase 267
// (`PageContent` multi-region hosting): a partial composition is a *set*
// of typed-tree view subtrees, rendered live (CSR) against a projected
// binding source, with no compiled module/solution behind them.
//
// A composition is IN PROGRESS by definition, so the preview must never
// throw: a subtree whose binding (or capability) isn't yet resolved renders
// a labelled placeholder, not an exception. The resolution DECISION is a
// pure, tier-neutral function (`outcome` / `outcomes`) so the composition
// logic is testable without a render; the Feliz rendering + the throw
// safety-net are the Fable-verified surface.
//
// Serves the typed-tree-view path only — a consumer whose modules use bare
// Feliz uses the standard React/Vite dev loop and needs none of this
// (GP 13). Wholly forge-public + tree-language-neutral (GP 1): a subtree is
// a label + its required binding keys + a render thunk; no tree type appears.

/// A single typed-tree view subtree in an unreduced (partial, not-yet-
/// compiled) composition.
type UnreducedViewSubtree = {
    /// Human label — shown on the placeholder when the subtree can't yet
    /// render, and as the subtree's heading in the live preview.
    Label: string
    /// The host-projected binding keys this subtree resolves against. The
    /// preview treats any unresolved required key as "not ready" and renders
    /// a labelled placeholder WITHOUT calling `Render` — so a partially
    /// projected composition degrades visibly instead of throwing.
    RequiredBindings: string list
    /// Render the subtree against the resolved projection. Only invoked when
    /// every `RequiredBindings` key resolves; still wrapped in a safety net
    /// so a throw (a capability not yet wired, a half-authored subtree)
    /// degrades to a placeholder rather than crashing the whole preview.
    Render: HostBindingSources -> ReactElement
}

/// The outcome of previewing one subtree: rendered live, or degraded to a
/// labelled placeholder naming the unresolved bindings. Tier-neutral so the
/// preview decision is inspectable without a render.
[<RequireQualifiedAccess>]
type PreviewOutcome =
    | Rendered
    | Placeholder of unresolved: string list

[<RequireQualifiedAccess>]
module UnreducedViewPreview =

    /// The subtree's required binding keys that DON'T resolve against
    /// `sources` (via the Phase 264 `tryResolve` — `QueryResults` shadows
    /// `State`). Empty ⇒ the subtree is ready to render.
    let unresolvedBindings (subtree: UnreducedViewSubtree) (sources: HostBindingSources) : string list =
        subtree.RequiredBindings
        |> List.filter (fun key -> HostBindingSources.tryResolve key sources |> Option.isNone)

    /// The pure preview decision for one subtree: `Rendered` when every
    /// required binding resolves, else `Placeholder` naming the unresolved
    /// keys. Tier-neutral (no Feliz) — the composition logic a test drives.
    let outcome (subtree: UnreducedViewSubtree) (sources: HostBindingSources) : PreviewOutcome =
        match unresolvedBindings subtree sources with
        | [] -> PreviewOutcome.Rendered
        | missing -> PreviewOutcome.Placeholder missing

    /// The per-subtree preview outcomes for a whole unreduced composition —
    /// the tier-neutral projection of what `render` produces (each subtree
    /// rendered live or degraded to a labelled placeholder). Re-evaluating
    /// this after editing a subtree needs no rebuild (it is a pure function
    /// of the current subtree set + projection) — the loop-speed property.
    let outcomes (subtrees: UnreducedViewSubtree list) (sources: HostBindingSources) : (string * PreviewOutcome) list =
        subtrees |> List.map (fun s -> s.Label, outcome s sources)

    /// The labelled placeholder a not-yet-resolvable subtree renders in place
    /// of its view — never an exception (the composition is in progress).
    let private placeholderElement (label: string) (unresolved: string list) : ReactElement =
        Html.div [
            prop.className "toolup-unreduced-preview-placeholder"
            prop.children [
                Html.strong [ prop.text label ]
                Html.span [ prop.text (sprintf "awaiting: %s" (String.concat ", " unresolved)) ]
            ]
        ]

    /// Render one subtree live, degrading to a labelled placeholder when a
    /// required binding is unresolved OR the render throws (a composition in
    /// progress must never crash the preview).
    let private renderSubtree (sources: HostBindingSources) (subtree: UnreducedViewSubtree) : ReactElement =
        match outcome subtree sources with
        | PreviewOutcome.Placeholder missing -> placeholderElement subtree.Label missing
        | PreviewOutcome.Rendered ->
            try
                subtree.Render sources
            with _ ->
                placeholderElement subtree.Label [ "render error" ]

    /// Render the whole unreduced composition's visible surface: every view
    /// subtree, live (CSR), against the projected `sources` — the partial
    /// composition's visible surface, re-rendered on each tree edit with no
    /// rebuild. A subtree not yet resolvable shows a labelled placeholder.
    let render (subtrees: UnreducedViewSubtree list) (sources: HostBindingSources) : ReactElement =
        Html.div [
            prop.className "toolup-unreduced-preview"
            prop.children (subtrees |> List.map (renderSubtree sources))
        ]