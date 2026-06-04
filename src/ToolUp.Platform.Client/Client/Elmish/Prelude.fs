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

#if FABLE_COMPILER
/// Fable-only timer shim used to schedule the next async tick. Not part of
/// the consumer-facing surface.
module internal Timer =
    open System.Timers

    let delay interval callback =
        let t = new Timer(float interval, AutoReset = false)
        t.Elapsed.Add callback
        t.Enabled <- true
        t.Start()
#endif

/// Default `Async.Start` shape used by `Cmd.OfAsync`. The upstream
/// `Cmd.OfAsyncWith` family parameterised this; ToolUp consumers always use
/// the default, so the parameterised family is dropped and this stays
/// internal.
module internal AsyncHelpers =
#if FABLE_COMPILER
    let start x =
        Timer.delay 1 (fun _ -> Async.StartImmediate x)
#else
    let inline start x = Async.Start x
#endif