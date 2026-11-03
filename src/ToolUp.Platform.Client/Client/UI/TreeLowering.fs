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
/// This file holds the authoring helpers, which use only `Fuaran.UI`'s node
/// vocabulary. The renderer is in `TreeLoweringRuntime`, reached only through
/// the dynamic import in `lower`: a production build puts it in a chunk the
/// browser fetches when the first tree surface mounts, so a shell whose first
/// screen draws no tree never downloads it.
///
/// While the chunk loads, `lower` shows a placeholder (`role="status"`,
/// `aria-busy`), the shape a grid or a chart shows while theirs loads. A chunk
/// that fails rejects into the nearest error boundary, which in the SDK shell is
/// `ModuleBoundary` and its "Reload module" button. `React.lazy` keeps a
/// rejection for good, so the rejected lazy is dropped here: the remount the
/// button causes builds a new one and the browser fetches the chunk again.
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

/// The renderer's tokens read from the shell's. The shell's own tokens are what
/// `HostThemeTokens` and a tenant palette drive, so a tree takes the deployment's
/// brand without a second theme. `fuaran-reference.css` states the structure and
/// is a host stylesheet; these lines only answer the colours and radii it asks for.
[<Literal>]
let internal themeBridge =
    ":root{"
    + "--fuaran-tone-default-fg:var(--text);--fuaran-tone-default-bg:var(--surface);"
    + "--fuaran-tone-default-border:var(--color-border);--fuaran-tone-subdued-fg:var(--muted);"
    + "--fuaran-tone-subdued-bg:var(--color-bg-light);--fuaran-tone-brand-fg:var(--color-brand);"
    + "--fuaran-tone-success-fg:var(--pos);--fuaran-tone-critical-fg:var(--neg);"
    + "--fuaran-radius-sm:var(--radius);--fuaran-radius-md:var(--radius);--fuaran-radius-lg:var(--radius);"
    + "--fuaran-focus-ring-color:var(--color-brand)}"

let private reactLazy: (unit -> JS.Promise<obj>) -> obj = import "lazy" "react"

[<Emit("$0.then(c => ({ default: c }), e => { $1(); throw e; })")>]
let private asLazyModule (load: JS.Promise<'c>) (onFailed: unit -> unit) : JS.Promise<obj> = jsNative

/// The real fetch of the renderer chunk: the dynamic import.
let internal importRuntime () : JS.Promise<obj> =
    unbox (importValueDynamic TreeLoweringRuntime.Tree)

// Sanctioned module-level mutable: how the chunk is fetched. Nothing but
// `useLoader` writes it, and outside tests nothing calls that.
let mutable private loadRuntime: unit -> JS.Promise<obj> = importRuntime

// Sanctioned module-level mutable: the one lazy component, replaced on failure.
let mutable private loadedTree: obj option = None

/// Fetch the renderer chunk through `load` from the next tree render on, or
/// through the real dynamic import again when `load` is `None`. A test uses it
/// to prove the failed-chunk path, which a real import cannot be made to take
/// on demand; the cached component is dropped so the next render asks again.
let internal useLoader (load: (unit -> JS.Promise<obj>) option) : unit =
    loadRuntime <- defaultArg load importRuntime
    loadedTree <- None

let private lazyTree () : obj =
    match loadedTree with
    | Some tree -> tree
    | None ->
        let tree =
            reactLazy (fun () -> asLazyModule (loadRuntime ()) (fun () -> loadedTree <- None))

        loadedTree <- Some tree
        tree

/// What a tree surface shows while the renderer chunk loads.
let private placeholder =
    Html.div [
        prop.role "status"
        prop.ariaBusy true
        prop.className "animate-pulse rounded bg-gray-100"
        prop.style [ style.minHeight 160 ]
    ]

/// Lower `node` to the `ReactElement` the module contract asks for.
let lower (host: HostBindingSources) (dispatch: 'Msg -> unit) (node: Node<'Msg>) : ReactElement =
    let props: TreeLoweringRuntime.TreeProps = {|
        Host = host
        Dispatch = unbox (box dispatch)
        Node = unbox (box node)
        IsIsland = isIsland
        RenderIsland = renderIsland
    |}

    React.Fragment [
        Html.style [ prop.text themeBridge ]
        React.Suspense([ ReactLegacy.createElement (unbox<ReactElement> (lazyTree ()), props) ], placeholder)
    ]

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