// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The half of the tree lowering that needs the Fuaran-UI renderer, in a file of its own so a production build loads it on demand.
/// `TreeLowering.lower` reaches this module only through a dynamic import, and
/// nothing else in the package may reference it: one static reference puts the
/// renderer's whole closure back in the shell's first load, which the
/// perf-budget gate reports as the renderer chunk turning static.
///
/// Everything this module needs from the authoring side arrives in the props,
/// so it references nothing in `TreeLowering` and the import runs one way.
module internal TreeLoweringRuntime

open Feliz
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Renderer
open ToolUp.Platform

/// What `TreeLowering.lower` hands the chunk. `Node` and `Dispatch` are the
/// caller's `Node<'Msg>` and `'Msg -> unit`, erased to `obj`: the message type
/// is a compile-time fact the chunk does not need. `IsIsland` and `RenderIsland`
/// answer the `Custom` nodes the authoring side's `island` registers.
type TreeProps = {|
    Host: HostBindingSources
    Dispatch: obj -> unit
    Node: Node<obj>
    IsIsland: string -> bool
    RenderIsland: string -> ReactElement
|}

// Sanctioned module-level mutable: the island lookup of the last render. The
// authoring side passes the same two functions on every render, and the
// runtime below is built once, so it reads them through this cell.
let mutable private islands: (string -> bool) * (string -> ReactElement) =
    (fun _ -> false), (fun _ -> Html.none)

/// The browser runtime with the island lookup in front of it, built on the
/// first tree render: a deployment that opens no tree surface never builds it.
/// `Action.Dispatch` is the only effect these trees carry, and it does not pass
/// the runtime's deny-by-default gate, so the gate stays closed.
let private runtime =
    lazy
        (let inner = BrowserRuntime.create ()

         let isIsland moduleId = (fst islands) moduleId

         let islandRenderer (componentId: string) (_: Map<string, JVal>) : ReactElement = (snd islands) componentId

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

/// The renderer's sources for a host projection. Only the two namespaces a
/// projection carries are filled; every other slot keeps its empty default.
let private sourcesOf (host: HostBindingSources) : BindingResolver.BindingSources = {
    BindingResolver.empty with
        QueryResults = host.QueryResults
        State = host.State
}

/// The component the chunk exports: the tree drawn through the renderer.
let Tree (props: TreeProps) : ReactElement =
    islands <- props.IsIsland, props.RenderIsland

    Render.render
        {
            Render.RenderContext.create (sourcesOf props.Host) props.Dispatch props.Node with
                Runtime = runtime.Value
        }
        props.Node