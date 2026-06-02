namespace ToolUp.Elmish.React

open Fable.Core
open Fable.Core.JsInterop
open Fable.React
open Browser.Dom
open ToolUp.Elmish

/// Render mode for the React renderer.
[<RequireQualifiedAccess>]
type internal AppMode =
    /// Render synchronously on every `setState` call. Default.
    | Sync
    /// Coalesce successive `setState` calls into a single render via
    /// `requestAnimationFrame`.
    | Batched
    /// Use `ReactDOM.hydrateRoot` (React 18) to hydrate a server-rendered
    /// tree rather than render from scratch.
    | Hydrate

[<AutoOpen>]
module internal Helpers =

    /// React 18 `createRoot` binding via Fable interop. `Fable.React` exposes
    /// this as `ReactDomClient.createRoot`; we use the binding shape directly
    /// so this package doesn't depend on a specific Fable.React minor version.
    [<Import("createRoot", "react-dom/client")>]
    let createRoot (container: Browser.Types.Element) : obj = jsNative

    [<Import("hydrateRoot", "react-dom/client")>]
    let hydrateRoot (container: Browser.Types.Element) (element: ReactElement) : obj = jsNative

    /// Resolve a DOM node by id; throws if absent (mirrors upstream behaviour
    /// — a missing placeholder is a deployment-shape defect, not a recoverable
    /// runtime case).
    let getElement (placeholderId: string) : Browser.Types.Element =
        match document.getElementById placeholderId with
        | null ->
            failwithf
                "ToolUp.Elmish.React: cannot find element with id '%s'. Add <div id=\"%s\"></div> to your index.html."
                placeholderId
                placeholderId
        | el -> el

[<RequireQualifiedAccess>]
module Program =

    /// Wire React rendering into the program's `setState` callback. The
    /// render mode determines whether successive dispatches batch or render
    /// individually.
    ///
    /// 0.4.1: also composes `Program.withDispatcherHandle` to install a
    /// `beforeunload` browser hook that calls `dispatcher.Terminate()` —
    /// so SSE listeners, notification streams, and clock-tick subs
    /// no-op cleanly on page navigation rather than firing one last
    /// async-loaded message at a dead React tree. The capture appends
    /// (does not clobber) so the consumer's `withDispatcherHandle` is
    /// preserved.
    let private withReactImpl
        (placeholderId: string)
        (mode: AppMode)
        (program: Program<'arg, 'model, 'msg, ReactElement>)
        =
        let mutable root: obj option = None
        let mutable lastModel: 'model option = None
        let mutable lastDispatch: Dispatch<'msg> option = None
        let mutable rafScheduled = false

        let actuallyRender () =
            match root, lastModel, lastDispatch with
            | Some r, Some m, Some d ->
                let view = Program.view program m d
                r?render (view) |> ignore
            | _ -> ()

        let setState (model: 'model) (dispatch: Dispatch<'msg>) =
            // First call: mount the root.
            if root.IsNone then
                let el = getElement placeholderId

                let mountedRoot =
                    match mode with
                    | AppMode.Hydrate ->
                        let initialView = Program.view program model dispatch
                        hydrateRoot el initialView
                    | AppMode.Sync
                    | AppMode.Batched -> createRoot el

                root <- Some mountedRoot

            lastModel <- Some model
            lastDispatch <- Some dispatch

            match mode with
            | AppMode.Batched ->
                if not rafScheduled then
                    rafScheduled <- true

                    window.requestAnimationFrame (fun _ ->
                        rafScheduled <- false
                        actuallyRender ())
                    |> ignore
            | AppMode.Sync
            | AppMode.Hydrate -> actuallyRender ()

        let installBeforeUnload (dispatcher: IDispatcher<'msg>) =
            // `beforeunload` fires on page navigation, tab close, and
            // explicit `window.location` swaps. `Terminate` is
            // idempotent, so a hot-reload that has already torn the
            // program down hits a safe no-op.
            window.addEventListener ("beforeunload", (fun _ -> dispatcher.Terminate()), false)

        program
        |> Program.withSetState setState
        |> Program.withDispatcherHandle installBeforeUnload

    /// Render synchronously on every `setState`. Use for most apps where
    /// dispatch frequency is low (UI events, RPC responses).
    let withReactSynchronous (placeholderId: string) (program: Program<'arg, 'model, 'msg, ReactElement>) =
        withReactImpl placeholderId AppMode.Sync program

    /// Coalesce successive renders via `requestAnimationFrame`. Use for apps
    /// where dispatch frequency is high (animation, drag, streamed updates).
    let withReactBatched (placeholderId: string) (program: Program<'arg, 'model, 'msg, ReactElement>) =
        withReactImpl placeholderId AppMode.Batched program

    /// Hydrate a server-rendered React tree into the placeholder rather than
    /// rendering from scratch. Use when `ToolUp.Platform.Client.Bootstrap.PrerenderExport`
    /// has emitted static HTML for this route.
    let withReactHydrate (placeholderId: string) (program: Program<'arg, 'model, 'msg, ReactElement>) =
        withReactImpl placeholderId AppMode.Hydrate program