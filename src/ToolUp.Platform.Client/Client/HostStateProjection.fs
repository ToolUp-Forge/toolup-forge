// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz
open ToolUp.Elmish

// ─── Phase 264 — host-state projection + binding (client / CSR) ────────
//
// The read-side complement to the Phase 110 `ClientHostCapabilities`
// seam. Phase 110 routes a hosted tree's typed ACTIONS; this routes its
// DATA. A host implements `IHostStateProjection` to project its Elmish
// `Model` into the neutral `HostBindingSources` namespace
// (`ToolUp.Platform.Core`); `ClientBoundHostView.withBoundElementView`
// threads that projection into the view alongside the capability bag, so
// the hosted runtime resolves bindings against host-projected state
// instead of re-deriving it or baking it into the tree.
//
// Strictly additive over Phase 110 (GP 11): `ClientHostView.withElementView`
// is untouched and a new function/interface is added beside it — every
// existing host compiles byte-for-byte unchanged. A pipeline that never
// calls `withBoundElementView` never constructs a projection and pays
// nothing (GP 13).

/// A host's projection of its Elmish `'Model` into the neutral
/// `HostBindingSources` namespace a hosted tree resolves bindings
/// against. The host owns projection; the tree owns resolution — the
/// host decides which model fields become `QueryResults` / `State`
/// entries, the tree only reads them by name.
type IHostStateProjection<'Model> =
    abstract Project: model: 'Model -> HostBindingSources

[<RequireQualifiedAccess>]
module IHostStateProjection =

    /// Build a projection from a plain function — the common case where a
    /// host has a `Model -> HostBindingSources` lambda and wants no
    /// object-expression ceremony.
    let ofFunc (project: 'Model -> HostBindingSources) : IHostStateProjection<'Model> =
        { new IHostStateProjection<'Model> with
            member _.Project model = project model
        }

/// `withElementView`-shaped builder step (Phase 110) extended with the
/// read-side: the view additionally receives the `HostBindingSources`
/// projected from the module's current `Model`. A separate module from
/// `ClientHostView` because an F# module name is unique within a
/// namespace — the action-side seam keeps its file, this adds the
/// read-side beside it without reopening it.
[<RequireQualifiedAccess>]
module ClientBoundHostView =

    /// Single-page full-width view that receives BOTH a
    /// `ClientHostCapabilities` (the Phase 110 action bag, built from the
    /// same dispatch) AND a `HostBindingSources` projected from the
    /// current `Model` via `projection`. The projection runs once per
    /// render, off the same `model` Elmish already threads to the view —
    /// no extra subscription, no re-derivation.
    ///
    /// Existing `ClientHostView.withElementView` callers are untouched
    /// (GP 11): this is an additive companion, not a signature change.
    ///
    /// ```
    /// ClientModule.create spec
    /// |> ClientBoundHostView.withBoundElementView
    ///        (IHostStateProjection.ofFunc (fun m ->
    ///            HostBindingSources.ofQueryResults (Map [ "count", box m.Count ])))
    ///        (fun model dispatch host sources ->
    ///            MyTreeRuntime.render (page model) host sources)
    /// |> ClientModule.register
    /// ```
    let withBoundElementView
        (projection: IHostStateProjection<'Model>)
        (view: 'Model -> ('Msg -> unit) -> ClientHostCapabilities<'Msg> -> HostBindingSources -> ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        ClientModule.withFullWidthView
            (fun model dispatch ->
                view model dispatch (ClientHostCapabilities.create dispatch) (projection.Project model))
            m

    // ─── Phase 267 — projection-bound multi-region hosting ─────────────
    //
    // The read-side (Phase 264) companions to `ClientHostView.withElementPanes`
    // / `withElementPages`: a hosted split-pane or multi-page module whose
    // every region ALSO receives the `HostBindingSources` projected from the
    // current `Model`. The projection runs ONCE per render (off the same
    // `model` Elmish threads to the view) and the SAME namespace is handed to
    // every region — so a binding resolves identically in either pane / on
    // every page (the Phase 267 acceptance that the projection threads
    // identically into each region). One dispatch → one capability bag, one
    // model → one projection, shared across regions.
    //
    // Additive over Phase 267 (GP 11): the capability-only `withElementPanes`
    // / `withElementPages` are untouched; a pipeline that never projects pays
    // nothing (GP 13).

    /// Split-pane host (`ClientHostView.withElementPanes`) whose `(control,
    /// output)` view additionally receives the `HostBindingSources` projected
    /// from the current `Model`. Both panes share one projection.
    let withBoundElementPanes
        (projection: IHostStateProjection<'Model>)
        (view:
            'Model
                -> ('Msg -> unit)
                -> ClientHostCapabilities<'Msg>
                -> HostBindingSources
                -> ReactElement * ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        ClientModule.withView
            (fun model dispatch ->
                view model dispatch (ClientHostCapabilities.create dispatch) (projection.Project model))
            m

    /// Multi-page host (`ClientHostView.withElementPages`) whose every page
    /// view additionally receives the `HostBindingSources` projected from the
    /// current `Model`. All pages share one `Model` (per `withPages`) and
    /// therefore one projection per render.
    let withBoundElementPages
        (projection: IHostStateProjection<'Model>)
        (pageViews:
            (PageConfig *
            ('Model -> ('Msg -> unit) -> ClientHostCapabilities<'Msg> -> HostBindingSources -> PageContent)) list)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        pageViews
        |> List.map (fun (page, view) ->
            page,
            (fun model dispatch ->
                view model dispatch (ClientHostCapabilities.create dispatch) (projection.Project model)))
        |> fun mapped -> ClientModule.withPages mapped m

// ─── Phase 299 — owning ComponentId on the hosting seam (identity bridge) ─
//
// A hosted view tree (Phase 110 / 264) rendered without knowing WHICH
// composition component it belonged to, so an op or a telemetry event
// (Phase 297 usage export) could not resolve across the composition ↔ view
// boundary — a node in a hosted view had no way back to the `ComponentId`
// (Phase 279) of the module that hosts it. This is the forge half of the
// identity bridge: the owning `ComponentId` is threaded onto the hosting
// seam so a hosted tree — and its interaction events + binding
// resolutions — carry it.
//
// Strictly additive (GP 11): the Phase 264 `withBoundElementView` is
// untouched; the tagged variant (`withOwnedBoundElementView`) is added
// beside it and an UNTAGGED host (`HostOwnership.untagged`, or an existing
// `withBoundElementView` caller) attributes to `None` — byte-for-byte the
// pre-299 behaviour. Zero cost when unused (GP 13). Wholly forge-public and
// vendor/tree-language-free (GP 1) — tagging a view with its owning
// component id is generic substrate (telemetry attribution, multi-surface
// routing), no banned-vocabulary token.

/// The owning-component identity threaded onto the hosting seam. `None` is
/// an UNTAGGED host — exactly the pre-299 behaviour (GP 11). Identity by
/// value (`ComponentId` is a structural `string` wrapper, Phase 279 / GP 12
/// rule 1), so it is safe to correlate across the wire.
type HostOwnership = { OwningComponent: ComponentId option }

[<RequireQualifiedAccess>]
module HostOwnership =

    /// An untagged host — attributes to `None`. The default for an existing
    /// `withBoundElementView` caller that supplies no id (GP 11).
    let untagged: HostOwnership = { OwningComponent = None }

    /// A host owned by `id` — its hosted tree, events, and binding
    /// resolutions attribute to that component.
    let ofComponent (id: ComponentId) : HostOwnership = { OwningComponent = Some id }

    /// The owning component id, if any.
    let owner (ownership: HostOwnership) : ComponentId option = ownership.OwningComponent

    /// Attribute a value — an interaction event, a render fault, a resolved
    /// binding — to the owning component: pair it with the owning id so a
    /// downstream telemetry export (Phase 297) or a binding resolver
    /// (Phase 264) can correlate the value across the composition ↔ view
    /// boundary. An untagged host yields `None`, so an attributed value from
    /// a pre-299 host is indistinguishable from an untagged one (GP 11).
    let attribute (ownership: HostOwnership) (value: 'a) : ComponentId option * 'a = ownership.OwningComponent, value

/// `ClientBoundHostView.withBoundElementView` extended with the owning
/// `ComponentId` — the identity-bridge seam. A separate module (an F# module
/// name is unique within a namespace) so the Phase 264 seam keeps its file
/// and this adds beside it.
[<RequireQualifiedAccess>]
module ClientOwnedHostView =

    /// Single-page full-width view that receives the Phase 110 capability
    /// bag, the Phase 264 `HostBindingSources` projection, AND the Phase 299
    /// `HostOwnership` (the owning component id), so a hosted tree — and the
    /// events / bindings it raises — can attribute to the module that hosts
    /// it. Pass `HostOwnership.untagged` (or use the Phase 264
    /// `withBoundElementView`) for the pre-299 behaviour; an untagged host is
    /// byte-for-byte unchanged (GP 11).
    ///
    /// ```
    /// ClientModule.create spec
    /// |> ClientOwnedHostView.withOwnedBoundElementView
    ///        (HostOwnership.ofComponent (ComponentId.ofModule "sales"))
    ///        (IHostStateProjection.ofFunc project)
    ///        (fun model dispatch host sources ownership ->
    ///            MyTreeRuntime.render (page model) host sources ownership)
    /// |> ClientModule.register
    /// ```
    let withOwnedBoundElementView
        (ownership: HostOwnership)
        (projection: IHostStateProjection<'Model>)
        (view:
            'Model
                -> ('Msg -> unit)
                -> ClientHostCapabilities<'Msg>
                -> HostBindingSources
                -> HostOwnership
                -> ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        ClientModule.withFullWidthView
            (fun model dispatch ->
                view model dispatch (ClientHostCapabilities.create dispatch) (projection.Project model) ownership)
            m