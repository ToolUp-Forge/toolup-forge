// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

open System

/// Lifetime of an `EffectHandle` registered via `Program.withEffect`.
[<RequireQualifiedAccess>]
type EffectLifetime =
    /// Lives until `Program.withTermination` triggers. The vast majority
    /// of one-shot subscriptions (SSE listeners, notification streams,
    /// browser-event hooks) fit this shape.
    | Program

    /// Lives until the named module is reset (team switch, tenant switch,
    /// module reload). The runtime invokes `Effect.disposeByModule moduleId`
    /// at reset boundaries; consumers can also dispose explicitly via
    /// `Effect.disposeById`.
    | Module of moduleId: string

    /// Caller manages disposal explicitly via `Effect.disposeById`.
    | Manual

/// Lifetime-aware one-shot subscription handle. Distinct from `Sub<'msg>`,
/// which is the upstream diff-on-model-change shape — `EffectHandle` is the
/// simpler register-once / dispose-on-lifetime-end shape that the survey
/// found ToolUp consumers using exclusively (via `Cmd.ofEffect (fun d ->
/// SomeClient.subscribe (M >> d) |> ignore)` patterns that never get torn
/// down).
///
/// Tracked by the runtime in a `Dictionary<EffectId, (Lifetime, IDisposable)>`
/// so `ToolUp.Elmish.HMR` can dispose by id on hot-reload (fixing the
/// well-known leak where the previous build's `EventSource` survives the
/// hot-replace) and team-switch can dispose by module without the consumer
/// remembering to.
type EffectHandle<'msg> = {
    /// Stable identifier — must be unique across the program. Conventional
    /// shape is `"<feature>-<purpose>"` (e.g. `"sse-events"`,
    /// `"notifications-stream"`, `"navigation-requests"`). Used by HMR
    /// to identify the prior-build handle for disposal.
    Id: string
    /// Start the effect. Called once at registration; the returned
    /// `IDisposable` is tracked by the runtime.
    Start: Dispatch<'msg> -> IDisposable
    /// When the runtime should dispose the effect.
    Lifetime: EffectLifetime
}

[<RequireQualifiedAccess>]
module EffectHandle =

    /// Convenience: program-lifetime effect (lives until termination).
    let programLifetime (id: string) (start: Dispatch<'msg> -> IDisposable) : EffectHandle<'msg> = {
        Id = id
        Start = start
        Lifetime = EffectLifetime.Program
    }

    /// Convenience: module-scoped effect (lives until the named module resets).
    let moduleLifetime (id: string) (moduleId: string) (start: Dispatch<'msg> -> IDisposable) : EffectHandle<'msg> = {
        Id = id
        Start = start
        Lifetime = EffectLifetime.Module moduleId
    }

    /// Convenience: manual-disposal effect (caller calls `Effect.disposeById`).
    let manual (id: string) (start: Dispatch<'msg> -> IDisposable) : EffectHandle<'msg> = {
        Id = id
        Start = start
        Lifetime = EffectLifetime.Manual
    }

    /// Lift a `Dispatch -> unit` (the non-disposable shape) into an
    /// `EffectHandle` by giving it an empty disposer. Use when the
    /// underlying subscription truly lives for the page lifetime and
    /// the consumer can't influence its teardown (e.g. an `addEventListener`
    /// where the handler captures dispatch).
    let ofFireAndForget (id: string) (lifetime: EffectLifetime) (start: Dispatch<'msg> -> unit) : EffectHandle<'msg> = {
        Id = id
        Start =
            fun d ->
                start d

                { new IDisposable with
                    member _.Dispose() = ()
                }
        Lifetime = lifetime
    }