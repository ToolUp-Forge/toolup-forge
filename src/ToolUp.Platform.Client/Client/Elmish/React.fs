// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish.React

open Fable.Core
open Fable.Core.JsInterop
open Fable.React
open Browser.Dom
open ToolUp.Elmish

/// Render mode for the React renderer.
///
/// Phase 851 — what the modes coalesce OVER. Since 851 the loop calls the
/// render hook once per DRAIN (`Program.runWithDispatch` paints when the
/// ring is empty, with the model the drain ended on), so every mode below
/// already sees one hook call per drain rather than one per message. The
/// modes differ in how they coalesce hook calls from SEVERAL drains:
///
///   * `Sync` — the default — coalesces the drains of one TASK: the view is
///     constructed once, on the microtask queue, however many drains the
///     task ran (a keyboard event whose handler dispatches twice, a socket
///     message that dispatches and whose subscription dispatches again).
///     React 18 batches the DOM commit already; this stops the F# `view`
///     running twice. Nothing waits on a timer: a microtask runs before the
///     browser returns to the event loop, so the paint is never a frame
///     late.
///   * `Batched` coalesces across TASKS to the frame, via
///     `requestAnimationFrame`. For streams that dispatch many times per
///     frame from many tasks (animation, drag, high-rate telemetry).
///   * `Hydrate` mounts by hydrating the server-rendered tree with the
///     FIRST model the hook sees (the boot paint, which the loop makes
///     before `init`'s command runs — so it is `init`'s model, the one the
///     server rendered), then behaves as `Sync`.
[<RequireQualifiedAccess>]
type internal AppMode =
    /// One view construction per task, via the microtask queue. Default.
    | Sync
    /// Coalesce successive `setState` calls into a single render via
    /// `requestAnimationFrame`.
    | Batched
    /// Use `ReactDOM.hydrateRoot` (React 18) to hydrate a server-rendered
    /// tree rather than render from scratch, then as `Sync`.
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

    /// `queueMicrotask` — runs the callback after the current task's
    /// synchronous work and before the browser returns to the event loop.
    /// Every supported browser and Node >= 11 have it.
    [<Emit("queueMicrotask($0)")>]
    let queueMicrotask (callback: unit -> unit) : unit = jsNative

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

/// Phase 851 — one pending view construction per queue, however many render
/// hook calls arrive before it runs. `enqueue` is the queue: `queueMicrotask`
/// for the default mode (one construction per task), `requestAnimationFrame`
/// for `Batched` (one per frame). The flag is cleared BEFORE the render so a
/// hook call the render itself provokes (a React effect dispatching
/// synchronously is not a thing, but a consumer's `setState` wrapper could)
/// schedules a fresh construction rather than being folded into a finished
/// one. Internal so the Fable pack can pin the coalescing on the transpiled
/// code (`RenderCoalescingTests`); the adapter below is its only production
/// caller.
type internal RenderScheduler(enqueue: (unit -> unit) -> unit) =
    let mutable scheduled = false

    /// A construction is queued and has not yet run.
    member _.Pending = scheduled

    /// Ask for `render` to run once, on the queue — a no-op while one is
    /// already pending. The `render` that runs is the LAST one requested
    /// only in the sense that it reads whatever state the caller keeps;
    /// the scheduler holds no model of its own.
    member _.Request(render: unit -> unit) =
        if not scheduled then
            scheduled <- true

            enqueue (fun () ->
                scheduled <- false
                render ())

[<RequireQualifiedAccess>]
module Program =

    /// Wire React rendering into the program's `setState` callback. The
    /// render mode determines how hook calls from several drains coalesce
    /// into one view construction (see `AppMode`).
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

        let actuallyRender () =
            match root, lastModel, lastDispatch with
            | Some r, Some m, Some d ->
                let view = Program.view program m d
                r?render (view) |> ignore
            | _ -> ()

        // The queue the mode coalesces over (see `AppMode`).
        let scheduler =
            match mode with
            | AppMode.Batched -> RenderScheduler(fun k -> window.requestAnimationFrame (fun _ -> k ()) |> ignore)
            | AppMode.Sync
            | AppMode.Hydrate -> RenderScheduler queueMicrotask

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
            scheduler.Request actuallyRender

        let installBeforeUnload (dispatcher: IDispatcher<'msg>) =
            // `beforeunload` fires on page navigation, tab close, and
            // explicit `window.location` swaps. `Terminate` is
            // idempotent, so a hot-reload that has already torn the
            // program down hits a safe no-op.
            window.addEventListener ("beforeunload", (fun _ -> dispatcher.Terminate()), false)

        program
        |> Program.withSetState setState
        |> Program.withDispatcherHandle installBeforeUnload

    /// The default renderer: one view construction per task. Since Phase
    /// 851 the loop hands this hook one model per drain, and this mode
    /// constructs the view once per task however many drains the task ran
    /// (via the microtask queue — never a timer, never a frame late). Use
    /// for every app whose dispatches come from UI events, RPC responses
    /// and subscriptions; the name is kept from upstream Elmish, where it
    /// meant "not `requestAnimationFrame`", and every consumer calls it.
    let withReactSynchronous (placeholderId: string) (program: Program<'arg, 'model, 'msg, ReactElement>) =
        withReactImpl placeholderId AppMode.Sync program

    /// Coalesce renders across tasks to the frame via
    /// `requestAnimationFrame`. Use only for apps that dispatch many times
    /// per frame from many tasks (animation, drag, streamed updates); the
    /// default already coalesces within a task.
    let withReactBatched (placeholderId: string) (program: Program<'arg, 'model, 'msg, ReactElement>) =
        withReactImpl placeholderId AppMode.Batched program

    /// Hydrate a server-rendered React tree into the placeholder rather than
    /// rendering from scratch, then render as `withReactSynchronous`. Use
    /// when `ToolUp.Platform.Bootstrap.PrerenderExport` has emitted static
    /// HTML for this route.
    let withReactHydrate (placeholderId: string) (program: Program<'arg, 'model, 'msg, ReactElement>) =
        withReactImpl placeholderId AppMode.Hydrate program