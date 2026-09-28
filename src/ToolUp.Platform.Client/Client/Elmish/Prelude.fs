// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

/// Cross-platform logging shim. Used internally by the runtime; not part of
/// the consumer-facing surface. `Log.onError` is the default seed for
/// `Program.onError` until the consumer overrides it via `withErrorReporter`.
module internal Log =

#if FABLE_COMPILER
    open Fable.Core.JS

    let onError (text: string, ex: exn) = console.error (text, ex)
    let toConsole (text: string, o: #obj) = console.log (text, o)
#else
    let onError (text: string, ex: exn) =
        System.Console.Error.WriteLine("{0}: {1}", text, ex)

    let toConsole (text: string, o: #obj) = printfn "%s: %A" text o
#endif

/// Default `Async.Start` shape used by `Cmd.OfAsync`, `Cmd.OfRemoting` and
/// `IDispatcher.DispatchAsync`. The upstream Cmd.OfAsyncWith family
/// parameterised this; ToolUp consumers always use the default, so the
/// parameterised family is dropped and this stays internal.
///
/// Phase 851.C — under Fable the async is started IMMEDIATELY. Until 851
/// it was started one `setTimeout 1` macrotask later (upstream's
/// `Timer.delay 1`, inherited so that a command could not dispatch back
/// into a loop that was still processing the message that issued it).
/// That hop cost every remote call a macrotask before the request even
/// began — Phase 849 measured it at 1.1 ms minimum and ~15 ms median on
/// Windows' default timer granularity — and it protects nothing here: the
/// loop's re-entrancy latch already makes a synchronous dispatch from a
/// command safe. `proofs/ElmishLoop.fst`'s **`reentrant_no_loss`** is the
/// theorem: a `dispatch` made while the latch is set queues its message
/// at the back of what is pending, logs it, and processes and paints
/// nothing, so a command whose body reaches a dispatch before its first
/// real await — `Cmd.OfAsync.perform` over an already-resolved value, a
/// remoting call answered from Phase 854's in-flight table — has its
/// message handed to `update` once, in order, by the drain that is
/// already running. `Async.StartImmediate` runs the body synchronously to
/// its first bind on a pending promise and continues on that promise's
/// resolution (a microtask), so nothing waits on a timer.
///
/// Phase 907 — the .NET branch is started IMMEDIATELY too, by the SAME
/// theorem. `Async.Start` schedules the whole body on the thread pool
/// unconditionally, even when the body never reaches a real await — so on
/// .NET a command's synchronous prefix used to run on a worker thread, at
/// an unspecified point after `dispatch` had already returned, instead of
/// inside the drain that issued it. `reentrant_no_loss` does not depend on
/// which host runs it: the re-entrancy latch is host-agnostic, so the
/// argument that makes the timer-hop unnecessary under Fable makes the
/// thread-pool hop unnecessary here too. `Async.StartImmediate` on .NET
/// runs the body synchronously, on the calling thread, to its first await
/// on a pending `Task`/`Async`, and continues the rest on that awaiter's
/// callback — so a fully synchronous body (no real await at all) now
/// completes, and dispatches, before `Async.Start`'s caller returns, on
/// the SAME thread, exactly as it already did under Fable.
module internal AsyncHelpers =
#if FABLE_COMPILER
    let start x = Async.StartImmediate x
#else
    let inline start x = Async.StartImmediate x
#endif