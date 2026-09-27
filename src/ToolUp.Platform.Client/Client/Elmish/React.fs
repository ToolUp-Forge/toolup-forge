// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish.React

open System
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

/// Phase 852 — one published value of a `ModelStore`: the model the loop
/// last painted and the dispatch it painted with. A new snapshot is minted
/// per PUBLISH and never between, so a reader comparing snapshots by
/// reference sees a change exactly when the loop published one — which is
/// the contract `useSyncExternalStore` requires of `getSnapshot`.
type StoreSnapshot<'model, 'msg> = {
    Model: 'model
    Dispatch: Dispatch<'msg>
}

/// Phase 852 — the model store a store-bound program publishes to
/// (`Program.withReactStore`). The loop's render hook hands the store the
/// model once per task (the same coalescing `withReactSynchronous` does);
/// the root component reads it through `useSyncExternalStore` and calls
/// `Program.view` on it; any component below can read a SLICE of it through
/// `ModelStore.useSelector`, and re-renders only when that slice changes
/// (by the comparison the reader names). The store holds no update logic
/// and makes no decision the MVU loop does: the loop hands it a model, and
/// how the tree reads that model is React's concern — so nothing under
/// `proofs/` models it.
///
/// Create one per program run, before the program is built, so the view
/// can close over it (`ModelStore.create ()`).
[<Sealed>]
type ModelStore<'model, 'msg>() =
    let listeners = ResizeArray<Action>()
    let mutable snapshot: StoreSnapshot<'model, 'msg> option = None

    // Both handed to React as-is, so both are created ONCE: a `subscribe`
    // whose identity changed per render would make React resubscribe on
    // every render.
    let subscribe =
        Func<Action, Action>(fun listener ->
            listeners.Add listener
            Action(fun () -> listeners.Remove listener |> ignore))

    let getSnapshot =
        Func<obj>(fun () ->
            match snapshot with
            | Some s -> box s
            | None -> null)

    /// The last published snapshot; `None` before the first publish.
    member _.Snapshot = snapshot

    /// `useSyncExternalStore`'s `subscribe` — stable for the store's life.
    member internal _.SubscribeFn: obj = box subscribe

    /// `useSyncExternalStore`'s `getSnapshot` — stable for the store's life.
    member internal _.SnapshotFn: obj = box getSnapshot

    /// Replace the snapshot and notify every subscriber, synchronously.
    /// The caller (the render hook) decides WHEN — once per task — so a
    /// burst of drains notifies once. A listener that unsubscribes while
    /// being notified does not disturb the walk (it runs over a copy).
    member internal _.Publish(model: 'model, dispatch: Dispatch<'msg>) =
        snapshot <- Some { Model = model; Dispatch = dispatch }

        for listener in listeners.ToArray() do
            listener.Invoke()

[<AutoOpen>]
module internal StoreBindings =

    [<Import("useSyncExternalStore", "react")>]
    let useSyncExternalStore (subscribe: obj, getSnapshot: obj, getServerSnapshot: obj) : obj = jsNative

    [<Import("useRef", "react")>]
    let useRef (initial: obj) : obj = jsNative

    [<Import("createElement", "react")>]
    let createElement (elementType: obj, props: obj) : ReactElement = jsNative

    /// Report an error the way an uncaught one is reported — to the
    /// window's `error` listeners and the console — without throwing into
    /// the caller: `reportError` where the host has it (every current
    /// browser), else a rethrow on the microtask queue.
    [<Emit("(typeof window !== 'undefined' && window && typeof window.reportError === 'function') ? window.reportError($0) : queueMicrotask(function () { throw $0; })")>]
    let reportUncaught (error: exn) : unit = jsNative

    /// Build a view inside a component render, keeping the push binding's
    /// behaviour when the view THROWS. The push binding constructs the
    /// view outside React (on the microtask queue), so a throwing view
    /// raised an uncaught error and left the last good tree on screen. A
    /// view built inside a component would instead make React unmount the
    /// whole root. So: remember the last element that built (`lastGood` is
    /// a `useRef` cell), and on a throw report the error as uncaught and
    /// render that element again. A throw on the FIRST build has nothing to
    /// hold and propagates, as a failing first render always did.
    let buildHoldingLastGood (lastGood: obj) (build: unit -> ReactElement) : ReactElement =
        try
            let element = build ()
            lastGood?current <- element
            element
        with error ->
            if isNull lastGood?current then
                reraise ()
            else
                reportUncaught error
                unbox<ReactElement> lastGood?current

    /// The store-bound root: reads the whole snapshot and renders
    /// `Program.view` on it (`render` closes over the program). Passing
    /// `getSnapshot` as the server snapshot too is what lets a hydrating
    /// mount read the first published model — the one the server rendered.
    let storeRoot (props: obj) : ReactElement =
        let snapshot =
            useSyncExternalStore (props?subscribe, props?getSnapshot, props?getSnapshot)

        let lastGood = useRef null
        buildHoldingLastGood lastGood (fun () -> (unbox<obj -> ReactElement> props?render) snapshot)

/// Phase 852 — reading a store.
[<RequireQualifiedAccess>]
module ModelStore =

    /// A fresh, unpublished store for one program run.
    let create<'model, 'msg> () : ModelStore<'model, 'msg> = ModelStore<'model, 'msg>()

    /// Reference identity — the comparison a selector over an immutable
    /// model wants: `Map.add` for one key leaves every other value the same
    /// object, and an `update` that changed nothing returns the same state.
    let refEquals (a: 'slice) (b: 'slice) : bool = obj.ReferenceEquals(a, b)

    /// A React hook: the slice `selector` picks out of the store's current
    /// model. The calling component re-renders when a publish produces a
    /// slice that `equals` says differs from the last one it read — and
    /// never otherwise, however often the store publishes. When `equals`
    /// says the new slice is the same, the PREVIOUS value is returned, so a
    /// selector that allocates (a tuple, an option) is still read as
    /// unchanged. The selector may be a fresh closure per render (it is
    /// re-applied when its identity changes); it must be pure.
    ///
    /// Call only from a component rendered under a store-bound root, after
    /// the store's first publish (the root mounts on it, so every
    /// descendant does).
    let useSelector
        (store: ModelStore<'model, 'msg>)
        (selector: 'model -> 'slice)
        (equals: 'slice -> 'slice -> bool)
        : 'slice =
        let cache = useRef null

        let read () : obj =
            let current =
                match store.Snapshot with
                | Some s -> s
                | None -> invalidOp "ModelStore.useSelector: the store has not published a model yet."

            let last = cache?current

            if
                not (isNull last)
                && obj.ReferenceEquals(last?snapshot, current)
                && obj.ReferenceEquals(last?selector, selector)
            then
                last?selection
            else
                let next = selector current.Model

                let selection =
                    if not (isNull last) && equals (unbox<'slice> last?selection) next then
                        last?selection
                    else
                        box next

                cache?current <-
                    createObj [ "snapshot" ==> current; "selector" ==> selector; "selection" ==> selection ]

                selection

        let getSelection = Func<obj>(read)
        unbox<'slice> (useSyncExternalStore (store.SubscribeFn, getSelection, getSelection))

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
    // ─── Phase 852 — the store binding (opt-in) ─────────────────────────
    //
    // `withReactImpl` pushes a freshly constructed view through
    // `root.render` on every construction, so React reconciles the whole
    // tree from the top each time. The store binding mounts ONE root
    // component, once, and publishes each model to a `ModelStore` instead;
    // the root reads the store through `useSyncExternalStore` and calls
    // `Program.view` on what it read. Rendering the root's view is still a
    // whole-tree construction — what changes is that a component below can
    // subscribe to its own slice (`ModelStore.useSelector`) and a
    // `React.memo` boundary can hold between a component and the root,
    // because the root is no longer the only thing that can re-render it.
    // The coalescing is the adapter's, unchanged: one publish per task (or
    // per frame), through the same `RenderScheduler`.
    let private withReactStoreImpl
        (store: ModelStore<'model, 'msg>)
        (placeholderId: string)
        (hydrate: bool)
        (program: Program<'arg, 'model, 'msg, ReactElement>)
        =
        let mutable mounted = false
        let mutable latest: ('model * Dispatch<'msg>) option = None

        let publish () =
            match latest with
            | Some(model, dispatch) -> store.Publish(model, dispatch)
            | None -> ()

        let scheduler = RenderScheduler queueMicrotask

        // Created once: the root's `render` prop never changes identity.
        let render (snapshot: obj) : ReactElement =
            let s = unbox<StoreSnapshot<'model, 'msg>> snapshot
            Program.view program s.Model s.Dispatch

        let setState (model: 'model) (dispatch: Dispatch<'msg>) =
            latest <- Some(model, dispatch)

            if not mounted then
                mounted <- true
                // The first model is published synchronously, before the
                // root mounts, so the root's first read — and a hydrating
                // mount's server snapshot — is this model: the boot paint,
                // `init`'s model, the one a server rendered.
                publish ()
                let el = getElement placeholderId

                let rootElement =
                    createElement (
                        box storeRoot,
                        createObj [
                            "subscribe" ==> store.SubscribeFn
                            "getSnapshot" ==> store.SnapshotFn
                            "render" ==> box render
                        ]
                    )

                if hydrate then
                    hydrateRoot el rootElement |> ignore
                else
                    (createRoot el)?render (rootElement) |> ignore
            else
                scheduler.Request publish

        let installBeforeUnload (dispatcher: IDispatcher<'msg>) =
            window.addEventListener ("beforeunload", (fun _ -> dispatcher.Terminate()), false)

        program
        |> Program.withSetState setState
        |> Program.withDispatcherHandle installBeforeUnload

    /// Phase 852 — opt in to the store binding: publish each model to
    /// `store` (once per task, as `withReactSynchronous` constructs once per
    /// task) and mount one root that reads it through
    /// `useSyncExternalStore`. `Program.view` is the root's read of the
    /// store; components below it may read slices with
    /// `ModelStore.useSelector` and re-render only when their slice changes.
    /// A program that does not call this renders exactly as before.
    let withReactStore
        (store: ModelStore<'model, 'msg>)
        (placeholderId: string)
        (program: Program<'arg, 'model, 'msg, ReactElement>)
        =
        withReactStoreImpl store placeholderId false program

    /// Phase 852 — `withReactStore`, mounting by hydrating a server-rendered
    /// tree (`hydrateRoot`) with the first published model.
    let withReactStoreHydrate
        (store: ModelStore<'model, 'msg>)
        (placeholderId: string)
        (program: Program<'arg, 'model, 'msg, ReactElement>)
        =
        withReactStoreImpl store placeholderId true program