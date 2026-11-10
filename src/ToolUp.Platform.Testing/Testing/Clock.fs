// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

open System

// ─── Clock ────────────────────────────────────────────────────────────
//
// A test that reads the time or waits for it takes a `Clock` instead of
// calling `DateTime.UtcNow` / `Thread.Sleep` / `Async.Sleep` itself. With
// `Clock.system` it behaves exactly as before; with `Clock.fixedAt` time
// stands still and every delay completes at once, so a timing assertion
// no longer depends on how loaded the machine is.

/// Where a test reads the time and how it waits.
type Clock = {
    /// The current instant.
    Now: unit -> DateTimeOffset
    /// Wait for `duration` to pass on this clock.
    Delay: TimeSpan -> Async<unit>
}

/// The clocks the Testing package ships.
[<RequireQualifiedAccess>]
module Clock =
    /// The machine's clock: `DateTimeOffset.UtcNow` and `Async.Sleep`.
    let system: Clock = {
        Now = fun () -> DateTimeOffset.UtcNow
        Delay = fun duration -> Async.Sleep duration
    }

    /// A clock stopped at `instant`: every read answers it, and every delay
    /// completes immediately without moving it.
    let fixedAt (instant: DateTimeOffset) : Clock = {
        Now = fun () -> instant
        Delay = fun _ -> async.Return()
    }

    /// Wait on `clock` until `condition` holds, re-checking every `interval`,
    /// for at most `backstop` of waiting. Answers whether it held. The wait
    /// is counted in delays taken, never read off `Now`, so it ends under a
    /// clock whose time does not move. For a condition no write signals (a
    /// store's state, a file appearing); a double's own writes are waited on
    /// through `Recording.WaitUntil`.
    let waitUntil (clock: Clock) (interval: TimeSpan) (backstop: TimeSpan) (condition: unit -> bool) : Async<bool> =
        let rec loop (waited: TimeSpan) = async {
            if condition () then
                return true
            elif waited >= backstop then
                return false
            else
                do! clock.Delay interval
                return! loop (waited + interval)
        }

        loop TimeSpan.Zero