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
/// This file holds the authoring helpers, which use `Fuaran.UI`'s node
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

/// What the renderer's `Tree` component takes for `node`, dispatching into
/// `dispatch` and resolving against `host`. A deferred surface's tree file
/// draws `TreeLoweringRuntime.Tree (treeProps host dispatch node)` directly:
/// that file is itself loaded on demand, so the renderer arrives with it.
let internal treeProps
    (host: HostBindingSources)
    (dispatch: 'Msg -> unit)
    (node: Node<'Msg>)
    : TreeLoweringRuntime.TreeProps =
    {|
        Host = host
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
let lower (host: HostBindingSources) (dispatch: 'Msg -> unit) (node: Node<'Msg>) : ReactElement =
    TreeSurface.deferred RuntimeKey (fun () -> loadRuntime ()) (treeProps host dispatch node)

/// Lower through the module's capability bag, so a tree dispatches exactly
/// where the rest of the hosted seam does.
let lowerHosted (host: HostBindingSources) (capabilities: ClientHostCapabilities<'Msg>) (node: Node<'Msg>) =
    lower host capabilities.Dispatch node

let private markdownPunctuation = "\\`*_{}[]()#+-.!|<>~"

/// Backslash-escape `text` for the markdown subset. The vocabulary has no plain
/// text node, so running text rides `Markdown`; a module id such as
/// `_sdk.Team_Manager` must read as itself rather than as emphasis.
let private escapeMarkdown (text: string) : string =
    text
    |> Seq.map (fun c ->
        if markdownPunctuation.IndexOf c >= 0 then
            "\\" + string c
        else
            string c)
    |> String.concat ""

/// A run of literal text: never interpreted as markdown.
let text (id: string) (value: string) : Node<'Msg> =
    Fuaran.markdown id (escapeMarkdown value)

let literal (value: string) : TextSource = TextSource.Literal value

/// A row of children, the layout every action bar on these surfaces uses.
let row (id: string) (children: Node<'Msg> list) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Flex(Orientation.Horizontal, true, Some 2)
            Children = children
    }

/// A column of children.
let column (id: string) (children: Node<'Msg> list) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 3)
            Children = children
    }

/// A headed card, the panel shape of a settings surface.
let card (id: string) (heading: string) (children: Node<'Msg> list) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Role = BoxRole.Card
            Heading = Some(literal heading)
            Children = children
    }

let button (id: string) (variant: ButtonVariant) (label: string) (disabled: bool) (msg: 'Msg) : Node<'Msg> =
    Fuaran.button id {
        Defaults.button with
            Label = literal label
            Variant = variant
            OnClick = Action.dispatch msg
            Disabled = if disabled then Some(Binding.Static(Some true)) else None
    }

/// A banner with a dismiss button. `Callout` carries no dismiss action of its
/// own, and the shell's banners route dismissal through the module's update.
let banner (id: string) (tone: ToneVariant) (body: string) (dismissLabel: string) (onDismiss: 'Msg) : Node<'Msg> =
    row id [
        Fuaran.callout (id + "-body") {
            Defaults.callout with
                Body = literal body
                Tone = tone
        }
        button (id + "-dismiss") ButtonVariant.Tertiary dismissLabel false onDismiss
    ]

let heading (id: string) (level: int) (value: string) : Node<'Msg> =
    Fuaran.heading id {
        Defaults.heading with
            Level = level
            Text = literal value
    }