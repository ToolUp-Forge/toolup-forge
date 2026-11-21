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
/// is a compile-time fact the chunk does not need. `Locale` is the shell's
/// resolved locale (`Binding.Format` reads it when a binding names none) and
/// `I18n` the host's resolver for `TextSource.I18n` keys, if it furnishes one.
/// `IsIsland` and `RenderIsland` answer the `Custom` nodes the authoring side's
/// `island` registers.
type TreeProps = {|
    Host: HostBindingSources
    Locale: string
    I18n: IHostI18nResolver option
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

/// The host's resolver as the renderer's: `args` arrive resolved, so each is
/// handed on as its string form.
let private rendererI18n (resolver: IHostI18nResolver) (locale: string) : II18nResolver =
    { new II18nResolver with
        member _.Resolve(key, args) =
            let args =
                args
                |> Option.map (Map.map (fun _ value -> string value))
                |> Option.defaultValue Map.empty

            resolver.Resolve key args (LocaleCode locale)
    }

/// The renderer's sources for a tree's props. Only the two namespaces a
/// projection carries, the locale and the i18n resolver are filled; every
/// other slot keeps its empty default.
let private sourcesOf (props: TreeProps) : BindingResolver.BindingSources = {
    BindingResolver.empty with
        QueryResults = props.Host.QueryResults
        State = props.Host.State
        Locale = props.Locale
        I18nResolver =
            match props.I18n with
            | Some resolver -> rendererI18n resolver props.Locale
            | None -> BindingResolver.empty.I18nResolver
}

/// What one drawn tree keeps between renders: the context it rendered with and
/// what that context was built from.
type internal KeptContext = {
    QueryResults: Map<string, obj>
    State: Map<string, obj>
    Locale: string
    I18n: IHostI18nResolver option
    Facts: Fuaran.UI.BindingWalk.RenderFacts<obj>
    Context: Render.RenderContext<obj>
}

let private sameRef (a: 'a) (b: 'a) = obj.ReferenceEquals(a, b)

let private sameResolver (a: IHostI18nResolver option) (b: IHostI18nResolver option) =
    match a, b with
    | None, None -> true
    | Some a, Some b -> sameRef a b
    | _ -> false

/// Whether two trees declare the same state seeds, selection seeds and
/// fragments. A context carries what its tree declares, so a tree declaring
/// something new needs a new one; a fragment is the same only as the same node.
let private sameDeclarations (a: Fuaran.UI.BindingWalk.RenderFacts<obj>) (b: Fuaran.UI.BindingWalk.RenderFacts<obj>) =
    a.StateSeeds = b.StateSeeds
    && a.SelectionSeeds = b.SelectionSeeds
    && List.length a.Fragments = List.length b.Fragments
    && List.forall2 (fun (n1, f1) (n2, f2) -> n1 = n2 && sameRef f1 f2) a.Fragments b.Fragments

/// The context to render `props.Node` with: `previous`'s while the projected
/// sources are the same objects, the locale and resolver unchanged and the tree
/// declares what it declared, else a new one. The subtree memo serves an
/// element only under the context it was built for, so keeping the context is
/// what lets a node a view kept render once. `dispatch` is the drawn tree's
/// stable dispatch, which forwards to the latest `props.Dispatch`.
let internal keepContext (previous: KeptContext option) (props: TreeProps) (dispatch: obj -> unit) : KeptContext =
    let prepared = Render.RenderContext.prepare props.Node

    match previous with
    | Some kept when
        sameRef kept.QueryResults props.Host.QueryResults
        && sameRef kept.State props.Host.State
        && kept.Locale = props.Locale
        && sameResolver kept.I18n props.I18n
        && sameDeclarations kept.Facts prepared.Facts
        ->
        kept
    | _ -> {
        QueryResults = props.Host.QueryResults
        State = props.Host.State
        Locale = props.Locale
        I18n = props.I18n
        Facts = prepared.Facts
        Context = {
            Render.RenderContext.ofPrepared prepared (sourcesOf props) dispatch with
                Runtime = runtime.Value
                SubtreeMemo = true
        }
      }

/// The component the chunk exports: the tree drawn through the renderer, with
/// the subtree memo on and its context kept across renders (`keepContext`).
/// It holds that context in hooks, so it runs inside a render: as the component
/// the lowering's lazy import resolves to, or called from the render of the
/// component a deferred surface's file exports.
let Tree (props: TreeProps) : ReactElement =
    islands <- props.IsIsland, props.RenderIsland

    let latest = React.useRef props.Dispatch
    latest.current <- props.Dispatch
    let dispatch = React.useRef (fun (msg: obj) -> latest.current msg)
    let kept = React.useRef None
    let context = keepContext kept.current props dispatch.current
    kept.current <- Some context
    Render.render context.Context props.Node