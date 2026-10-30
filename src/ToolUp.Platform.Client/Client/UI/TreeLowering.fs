// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1160 - the one seam a forge surface authored as a Fuaran-UI tree
/// reaches the shell through. Internal on purpose: the module view contract
/// still returns `ReactElement`, so a tree is lowered at the edge of the view
/// and nothing public names `Node`.
///
/// A tree's events are `Action.Dispatch` into the module's own Elmish dispatch
/// (`ClientHostCapabilities.Dispatch`) and its data resolves against the
/// `HostBindingSources` the module's `IHostStateProjection` projects, so the
/// tree reaches the host through the seams every hosted runtime uses.
module internal TreeLowering

open Feliz
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Renderer
open ToolUp.Platform

/// The islands the current trees render, keyed by id. A `Custom` node's props are
/// wire data, so an island's closure over its live model and dispatch cannot ride
/// the node: it is refreshed here on each render and the node names it by id.
let private islands =
    System.Collections.Generic.Dictionary<string, unit -> ReactElement>()

[<Literal>]
let private IslandModule = "toolup"

let private islandRenderer (componentId: string) (_: Map<string, JVal>) : ReactElement =
    match islands.TryGetValue componentId with
    | true, render -> render ()
    | _ -> Html.none

/// The browser runtime with the island lookup in front of it, built on first
/// render: a deployment that opens none of these surfaces pays nothing (GP 13).
/// `Action.Dispatch` is the only effect these trees carry, and it does not pass
/// the runtime's deny-by-default gate, so the gate stays closed.
let private runtime =
    lazy
        (let inner = BrowserRuntime.create ()

         let isIsland moduleId = moduleId = IslandModule

         { new Runtime.IFuaranRuntime with
             member _.Call(endpoint, onResult) = inner.Call(endpoint, onResult)
             member _.Notify(channel, payload) = inner.Notify(channel, payload)
             member _.Navigate route = inner.Navigate route
             member _.SetState(key, value) = inner.SetState(key, value)
             member _.InvokeAiTool(toolName, args) = inner.InvokeAiTool(toolName, args)
             member _.WriteToClipboard text = inner.WriteToClipboard text

             member _.ReadFileBody(file, encoding, onRead) =
                 inner.ReadFileBody(file, encoding, onRead)

             member _.Warn message = inner.Warn message
             member _.LayoutObserver = inner.LayoutObserver

             member _.TryRenderCustom(moduleId, componentId, props) =
                 if isIsland moduleId then
                     Some(islandRenderer componentId props)
                 else
                     inner.TryRenderCustom(moduleId, componentId, props)

             member _.TryGetCustomRenderer(moduleId, componentId) =
                 if isIsland moduleId then
                     Some(islandRenderer componentId, None)
                 else
                     inner.TryGetCustomRenderer(moduleId, componentId)

             member _.TryRenderCustomInScope(scope, moduleId, componentId, props) =
                 if isIsland moduleId then
                     Some(islandRenderer componentId props)
                 else
                     inner.TryRenderCustomInScope(scope, moduleId, componentId, props)

             member _.TryGetCustomRendererInScope(scope, moduleId, componentId) =
                 if isIsland moduleId then
                     Some(islandRenderer componentId, None)
                 else
                     inner.TryGetCustomRendererInScope(scope, moduleId, componentId)

             member _.CanDispatch action = inner.CanDispatch action
             member _.TryLoadGuest scopeId = inner.TryLoadGuest scopeId
         })

/// A region that stays React, as a `Custom` node. `render` runs inside the tree's
/// render and may mount a component that owns hooks.
let island (id: string) (render: unit -> ReactElement) : Node<'Msg> =
    islands[id] <- render
    Fuaran.custom id IslandModule id Map.empty None []

/// The renderer's sources for a host projection. Only the two namespaces a
/// projection carries are filled; every other slot keeps its empty default.
let private sourcesOf (host: HostBindingSources) : BindingResolver.BindingSources = {
    BindingResolver.empty with
        QueryResults = host.QueryResults
        State = host.State
}

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

/// Lower `node` to the `ReactElement` the module contract asks for.
let lower (host: HostBindingSources) (dispatch: 'Msg -> unit) (node: Node<'Msg>) : ReactElement =
    React.Fragment [
        Html.style [ prop.text themeBridge ]
        Render.render
            {
                Render.RenderContext.create (sourcesOf host) dispatch node with
                    Runtime = runtime.Value
            }
            node
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