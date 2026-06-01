namespace Elmish.HMR

open Fable.Core
open Fable.Core.JsInterop
open Elmish

/// Vite / webpack HMR detection + state hand-off across module replaces.
///
/// 0.4.0's first cut gated on `window.__TOOLUP_ELMISH_HMR__`, which was
/// never set anywhere — so `Program.run`'s HMR-aware branch was dead
/// code and the headline value-prop (dispose previous build's effect
/// handles + dispatcher on hot-reload) was silently inert.
///
/// 0.4.1 detects HMR directly via `import.meta.hot` (Vite / Rollup) and
/// `module.hot` (webpack v4/v5). Both are bundler-injected at build
/// time; in a production bundle neither resolves, so the HMR path is
/// genuinely inert there without any feature flag.
///
/// The previous-build dispatcher is stashed on a `globalThis`-scoped
/// slot so it survives the module replace; on the next build's start
/// we read and `IDispatcher.Terminate()` it (which the runtime wires
/// to subs.stop + effectRegistry.DisposeAll + state-terminate +
/// MarkTerminated — i.e. the full teardown path that prevents the
/// SSE / notifications / clock-tick leak).
[<AutoOpen>]
module internal HmrDetection =

    /// Runtime check: is HMR available in this build? Vite sets
    /// `import.meta.hot` to a truthy `HotApi`; webpack sets `module.hot`;
    /// both are statically replaced with `undefined` in production builds.
    let internal hmrAvailable () : bool =
        emitJsExpr
            ()
            """(function () {
                try {
                    if (typeof import.meta !== 'undefined' && import.meta && import.meta.hot) return true;
                } catch (e) {}
                try {
                    if (typeof module !== 'undefined' && module.hot) return true;
                } catch (e) {}
                return false;
            })()"""

    /// `globalThis`-scoped slot that survives module-replaces. Stores the
    /// previous build's dispatcher Terminate callback so the next build
    /// can flip the old loop off before booting the new one.
    let internal getHmrSlot () : obj =
        emitJsExpr
            ()
            """(globalThis.__TOOLUP_ELMISH_HMR_SLOT__ = globalThis.__TOOLUP_ELMISH_HMR_SLOT__ || { terminate: null })"""

    let internal stashTerminate (terminate: unit -> unit) : unit =
        let slot = getHmrSlot ()
        slot?terminate <- terminate

    let internal callPreviousTerminate () : unit =
        let slot = getHmrSlot ()
        let prior: obj = slot?terminate

        if not (isNull prior) then
            try
                prior $ () |> ignore
            with _ ->
                ()

            slot?terminate <- null

    /// Vite's `import.meta.hot.dispose` callback registration. When the
    /// current build is about to be replaced, the bundler calls this
    /// callback first so we can tear the program down before the new
    /// module evaluates. webpack's `module.hot.dispose` has the same
    /// shape; we register against whichever is present.
    let internal registerDisposeCallback (callback: unit -> unit) : unit =
        emitJsStatement
            callback
            """(function () {
                try {
                    if (typeof import.meta !== 'undefined' && import.meta && import.meta.hot && typeof import.meta.hot.dispose === 'function') {
                        import.meta.hot.dispose(function () { $0(); });
                        return;
                    }
                } catch (e) {}
                try {
                    if (typeof module !== 'undefined' && module.hot && typeof module.hot.dispose === 'function') {
                        module.hot.dispose(function () { $0(); });
                        return;
                    }
                } catch (e) {}
            })()"""

[<RequireQualifiedAccess>]
module Program =

    /// Compose `withDispatcherHandle` to capture the dispatcher and
    /// register a hot-reload-time `Terminate` so the previous build's
    /// program tears down cleanly before the next build boots.
    ///
    /// Appends — does not clobber an existing `withDispatcherHandle` sink.
    /// The runtime's `dispatcherHandleSinks` is a list (0.4.1), so the
    /// consumer's sink and the React adapter's sink both fire alongside
    /// this one.
    let private withHmrDispatcherCapture (program: Program<'arg, 'model, 'msg, 'view>) =
        program
        |> Program.withDispatcherHandle (fun dispatcher ->
            // Stash the dispatcher's Terminate so the NEXT build can call
            // it; also register a dispose callback against the current
            // bundler so the CURRENT build tears down on hot-replace.
            let terminate = fun () -> dispatcher.Terminate()
            stashTerminate terminate
            registerDisposeCallback terminate)

    /// HMR-aware wrapper around `Elmish.Program.run`. In a Vite / webpack
    /// dev build, tears the previous program down (disposing every
    /// `EffectHandle` and stopping every active subscription) before
    /// starting the new one — so background SSE / notification listeners
    /// don't leak across hot reloads.
    ///
    /// In production builds (no `import.meta.hot` / `module.hot`), this
    /// is identical to `Elmish.Program.run`.
    let run (program: Program<unit, 'model, 'msg, 'view>) =
        if hmrAvailable () then
            callPreviousTerminate ()
            program |> withHmrDispatcherCapture |> Elmish.Program.run
        else
            Elmish.Program.run program

    /// HMR-aware `runWith`.
    let runWith (arg: 'arg) (program: Program<'arg, 'model, 'msg, 'view>) =
        if hmrAvailable () then
            callPreviousTerminate ()
            program |> withHmrDispatcherCapture |> Elmish.Program.runWith arg
        else
            Elmish.Program.runWith arg program

    /// HMR-aware `withReactSynchronous` — passes through to the React
    /// renderer; the HMR teardown is wired separately at `Program.run`.
    let inline withReactSynchronous
        (placeholderId: string)
        (program: Program<'arg, 'model, 'msg, Fable.React.ReactElement>)
        =
        Elmish.React.Program.withReactSynchronous placeholderId program

    /// HMR-aware `withReactBatched`.
    let inline withReactBatched
        (placeholderId: string)
        (program: Program<'arg, 'model, 'msg, Fable.React.ReactElement>)
        =
        Elmish.React.Program.withReactBatched placeholderId program

    /// HMR-aware `withReactHydrate`.
    let inline withReactHydrate
        (placeholderId: string)
        (program: Program<'arg, 'model, 'msg, Fable.React.ReactElement>)
        =
        Elmish.React.Program.withReactHydrate placeholderId program