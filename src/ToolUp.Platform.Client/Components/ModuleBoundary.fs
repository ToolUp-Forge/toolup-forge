// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.ModuleBoundary

open Fable.Core
open Fable.Core.JsInterop
open Fable.React
open Feliz
open ToolUp.Platform

let private log = Logger.forCategory "client.module-boundary"

// Phase 12c — module-level React error boundary.
//
// Today, a single F# or React render-time exception in any module's view
// crashes the entire Elmish shell (every other module loses state, full page
// reload required). This boundary contains the failure: a runtime exception
// in module N's view stays inside module N's sidebar entry, surfacing a
// localised error UI with a "Reload module" button while every other module
// keeps working untouched.
//
// Mechanism: pure F# class component inheriting `Fable.React.Component<...>`
// with `componentDidCatch` overridden. The throwing render call is in a
// CHILD function component (`ModuleViewHost`) so the exception bubbles into
// the boundary's lifecycle (a class component cannot catch its own render
// errors). React's reconciler routes both sync F# exceptions during the
// view-function call AND React render-time exceptions in the produced tree
// into `componentDidCatch`.
//
// Note on `getDerivedStateFromError`: `Fable.React.Types` exposes only
// `componentDidCatch`; the static `getDerivedStateFromError` lifecycle is
// not in the F# binding. Using `componentDidCatch` + `setState` instead.
// React 19 renders the boundary's fallback synchronously after
// `componentDidCatch` returns, so the brief blank-frame risk is minor.

type private BoundaryProps = {|
    ModuleId: string
    Messages: ModuleBoundaryMessages
    OnError: (ModuleErrorReport -> unit) option
    OnReload: unit -> unit
    RenderInner: unit -> PageContent
    InputsWidth: InputsPaneWidth
|}

type private BoundaryState = {|
    Error: exn option
    ComponentStack: string
|}

/// Function-component child that owns the throwing render call. Sync F#
/// exceptions during `renderInner ()` and React render-time errors anywhere
/// in the produced tree both bubble to the parent class-component
/// boundary's `componentDidCatch`.
[<ReactComponent>]
let private ModuleViewHost (inputsWidth: InputsPaneWidth) (renderInner: unit -> PageContent) : ReactElement =
    Toolup.UIToolkit.Layout.renderPageContent inputsWidth (renderInner ())

type private Boundary(initialProps: BoundaryProps) =
    inherit Component<BoundaryProps, BoundaryState>(initialProps)

    do base.setInitState ({| Error = None; ComponentStack = "" |})

    override this.componentDidCatch(error: System.Exception, info: obj) =
        let stack =
            try
                string info?componentStack
            with _ ->
                ""

        this.setState (fun _ _ -> {|
            Error = Some error
            ComponentStack = stack
        |})

        match this.props.OnError with
        | Some f ->
            f {
                ModuleId = this.props.ModuleId
                Error = error
                ComponentStack = stack
            }
        | None ->
            // Dev-diagnostics fallback when no telemetry is wired. When OnError
            // is set, the deployment owns logging entirely (no double-log).
            log.Error(
                sprintf
                    "%s crashed: %s\n%s\nComponent stack:%s"
                    this.props.ModuleId
                    error.Message
                    error.StackTrace
                    stack,
                Some error
            )

    override this.render() : ReactElement =
        match this.state.Error with
        | None ->
            // ModuleViewHost is the throwing-call host; calling it as a Feliz
            // [<ReactComponent>] returns a React element that React mounts as
            // a child component, so render-time errors propagate to this
            // boundary's componentDidCatch.
            ModuleViewHost this.props.InputsWidth this.props.RenderInner
        | Some _ ->
            Html.div [
                prop.className "p-6 min-h-full flex flex-col items-center justify-center gap-4 text-gray-700"
                prop.children [
                    Html.h2 [
                        prop.className "text-lg font-semibold"
                        prop.text this.props.Messages.Heading
                    ]
                    Html.p [
                        prop.className "text-sm text-gray-500 max-w-prose text-center"
                        prop.text this.props.Messages.Body
                    ]
                    Html.button [
                        prop.className "px-4 py-2 rounded bg-gray-900 text-white text-sm hover:bg-gray-700"
                        prop.text this.props.Messages.Reload
                        prop.onClick (fun _ -> this.props.OnReload())
                    ]
                ]
            ]

/// Wrap a module's view-function thunk in a React error boundary.
///
/// `resetKey` participates in the React `key` so any state change the caller
/// considers "this should be a fresh boundary instance" forces React to
/// unmount the old instance and mount a new one with `Error = None`. Two
/// such state changes today: incrementing the per-module reset counter
/// (Reload-button click) and switching the active team (the boundary would
/// otherwise survive the team swap with stale `Error = Some`). Composing
/// both into a single string keeps the boundary agnostic to either reason.
///
/// The thunk is invoked inside the boundary's child component so both sync
/// F# exceptions during `pageView state dispatch` AND React render-time
/// exceptions in the produced tree route into `componentDidCatch`.
let wrapWith
    (msgs: ModuleBoundaryMessages)
    (moduleId: string)
    (resetKey: string)
    (onError: (ModuleErrorReport -> unit) option)
    (onReload: unit -> unit)
    (inputsWidth: InputsPaneWidth)
    (renderInner: unit -> PageContent)
    : ReactElement =
    let propsWithKey: obj =
        !!{|
            key = sprintf "%s#%s" moduleId resetKey
            ModuleId = moduleId
            OnError = onError
            OnReload = onReload
            RenderInner = renderInner
            InputsWidth = inputsWidth
            Messages = msgs
        |}

    ReactLegacy.createElement (unbox<ReactElement> (jsConstructor<Boundary>), propsWithKey)

/// Back-compat entry point — the pre-444 signature, rendering the
/// built-in English fallback text. A NEW function rather than an added
/// parameter on `wrap`, because `wrap` is public surface and widening
/// its arity reads as a removal in the public-API baseline. The shell
/// calls `wrapWith`.
let wrap
    (moduleId: string)
    (resetKey: string)
    (onError: (ModuleErrorReport -> unit) option)
    (onReload: unit -> unit)
    (inputsWidth: InputsPaneWidth)
    (renderInner: unit -> PageContent)
    : ReactElement =
    wrapWith MessageCatalog.english.ModuleBoundary moduleId resetKey onError onReload inputsWidth renderInner