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