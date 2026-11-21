// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The one seam a forge surface authored as a Fuaran-UI tree reaches the shell
/// through. Internal on purpose: the module view contract still returns
/// `ReactElement`, so a tree is lowered at the edge of the view and nothing
/// public names `Node`.
///
/// A tree's events are `Action.Dispatch` into the module's own Elmish dispatch
/// (`ClientHostCapabilities.Dispatch`) and its data resolves against the
/// `HostBindingSources` the module's `IHostStateProjection` projects, so the
/// tree reaches the host through the seams every hosted runtime uses.
///
/// This file holds the island helper and the props, which use `Fuaran.UI`'s node
/// vocabulary, so it is itself NOT free to import at boot: the vocabulary and
/// its closure weigh about 370 KB. The renderer is in `TreeLoweringRuntime`,
/// reached from `lower` only through a dynamic import, so `lower` from a file
/// the shell imports at boot keeps the renderer off the first load and pays
/// for the vocabulary. A surface that should cost the first load nothing builds
/// its tree in a file of its own and mounts that through `TreeSurface.deferred`
/// (see there); the loading and failed-load states are `TreeSurface`'s.
module internal TreeLowering

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.Platform

/// The islands the current trees render, keyed by id. A `Custom` node's props are
/// wire data, so an island's closure over its live model and dispatch cannot ride
/// the node: it is refreshed here on each render and the node names it by id.
let private islands =
    System.Collections.Generic.Dictionary<string, unit -> ReactElement>()

[<Literal>]
let private IslandModule = "toolup"

let private isIsland (moduleId: string) = moduleId = IslandModule

let private renderIsland (componentId: string) : ReactElement =
    match islands.TryGetValue componentId with
    | true, render -> render ()
    | _ -> Html.none

/// A region that stays React, as a `Custom` node. `render` runs inside the tree's
/// render and may mount a component that owns hooks.
let island (id: string) (render: unit -> ReactElement) : Node<'Msg> =
    islands[id] <- render
    Fuaran.custom id IslandModule id Map.empty None []

/// The real fetch of the renderer chunk: the dynamic import.
let internal importRuntime () : JS.Promise<obj> =
    unbox (importValueDynamic TreeLoweringRuntime.Tree)

// Sanctioned module-level mutable: how the chunk is fetched. Nothing but
// `useLoader` writes it, and outside tests nothing calls that.
let mutable private loadRuntime: unit -> JS.Promise<obj> = importRuntime

[<Literal>]
let private RuntimeKey = "TreeLoweringRuntime.Tree"

/// Fetch the renderer chunk through `load` from the next tree render on, or
/// through the real dynamic import again when `load` is `None`. A test uses it
/// to prove the failed-chunk path, which a real import cannot be made to take
/// on demand; the cached component is dropped so the next render asks again.
let internal useLoader (load: (unit -> JS.Promise<obj>) option) : unit =
    loadRuntime <- defaultArg load importRuntime
    TreeSurface.forget RuntimeKey

/// What a tree resolves against beyond its own nodes: the data the module's
/// `IHostStateProjection` projects, the shell's locale, and the host's i18n
/// resolver for `TextSource.I18n` keys, when it furnishes one.
type internal TreeHost = {
    Sources: HostBindingSources
    Locale: string
    I18n: IHostI18nResolver option
}

[<RequireQualifiedAccess>]
module internal TreeHost =

    /// `sources` with no locale (the runtime default) and no resolver.
    let ofSources (sources: HostBindingSources) : TreeHost = {
        Sources = sources
        Locale = ""
        I18n = None
    }

    /// `sources` under the locale of the module view's `RenderContext`.
    let ofContext (ctx: RenderContext<'Msg>) (sources: HostBindingSources) : TreeHost = {
        ofSources sources with
            Locale = ctx.Locale
    }

    /// `host` resolving `TextSource.I18n` keys through `resolver`.
    let withI18n (resolver: IHostI18nResolver) (host: TreeHost) : TreeHost = { host with I18n = Some resolver }

/// What the renderer's `Tree` component takes for `node`, dispatching into
/// `dispatch` and resolving against `host`. A deferred surface's tree file
/// draws `TreeLoweringRuntime.Tree (treeProps host dispatch node)` directly:
/// that file is itself loaded on demand, so the renderer arrives with it.
let internal treeProps (host: TreeHost) (dispatch: 'Msg -> unit) (node: Node<'Msg>) : TreeLoweringRuntime.TreeProps = {|
    Host = host.Sources
    Locale = host.Locale
    I18n = host.I18n
    Dispatch = unbox (box dispatch)
    Node = unbox (box node)
    IsIsland = isIsland
    RenderIsland = renderIsland
|}

/// Lower `node` to the `ReactElement` the module contract asks for, the
/// renderer loading on demand behind `TreeSurface.deferred`. The tree's
/// structure and theme come from the host's stylesheet (`fuaran-reference.css`
/// and the shell's `fuaran-bridge.css`, imported once by the CSS entry), so a
/// lowered tree emits no style element of its own.
///
/// The renderer's subtree memo is on and the drawn tree keeps its context while
/// `host`'s projected maps are the same objects, so a node the view kept (see
/// `Toolup.UIToolkit.Keep`) renders once until it is replaced.
let lower (host: TreeHost) (dispatch: 'Msg -> unit) (node: Node<'Msg>) : ReactElement =
    TreeSurface.deferred RuntimeKey (fun () -> loadRuntime ()) (treeProps host dispatch node)

/// Lower through the module's capability bag, so a tree dispatches exactly
/// where the rest of the hosted seam does.
let lowerHosted (host: TreeHost) (capabilities: ClientHostCapabilities<'Msg>) (node: Node<'Msg>) =
    lower host capabilities.Dispatch node