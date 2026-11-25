// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

open System

/// Dispatch — feed a new message into the processing loop.
type Dispatch<'msg> = 'msg -> unit

/// Effect — returns immediately, may schedule dispatch of a message at any time.
type Effect<'msg> = Dispatch<'msg> -> unit

/// Cmd — container for effects that may produce messages.
type Cmd<'msg> = Effect<'msg> list

/// `Cmd` module — create and manipulate commands.
///
/// Trimmed from upstream Elmish v5.x: the `OfFunc`, `OfPromise`, `OfTask`,
/// `OfValueTask`, `OfAsyncWith`, and `OfAsyncImmediate` families are dropped
/// (zero observed call sites across the ToolUp consumer base; see
/// `MIGRATION.md` for replacement shapes). Remote calls are effect values
/// (`RemoteEffect`), not commands.
[<RequireQualifiedAccess>]
module Cmd =

    /// Internal — execute every effect in a `Cmd`, routing exceptions to
    /// `onError`. Invoked by the runtime; not part of the consumer surface.
    let internal exec onError (dispatch: Dispatch<'msg>) (cmd: Cmd<'msg>) =
        cmd
        |> List.iter (fun call ->
            try
                call dispatch
            with ex ->
                onError ex)

    /// `Cmd.none` — no commands, equivalent to `[]`.
    let none: Cmd<'msg> = []

    /// Issue a specific message.
    let inline ofMsg (msg: 'msg) : Cmd<'msg> = [
        fun dispatch -> dispatch msg
    ]

    /// Wrap an arbitrary effect as a command.
    let ofEffect (effect: Effect<'msg>) : Cmd<'msg> = [ effect ]

    /// When emitting the message, map it to another type.
    let map (f: 'a -> 'msg) (cmd: Cmd<'a>) : Cmd<'msg> =
        cmd |> List.map (fun g -> (fun dispatch -> f >> dispatch) >> g)

    /// Aggregate multiple commands.
    let batch (cmds: #seq<Cmd<'msg>>) : Cmd<'msg> = cmds |> List.concat

    /// `Cmd.OfAsync` — wrap an async computation as a command.
    /// Only `either`, `perform`, `attempt` survive from upstream — the
    /// parameterised `OfAsyncWith` and Fable-only `OfAsyncImmediate` are
    /// dropped (consumers always used the default `start`).
    module OfAsync =

        /// Evaluate an async block and map the result into success or
        /// error (of exception).
        let either (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch = async {
                try
                    let! r = task arg
                    dispatch (ofSuccess r)
                with ex ->
                    dispatch (ofError ex)
            }

            [ bind >> AsyncHelpers.start ]

        /// Evaluate an async block and dispatch the success message;
        /// errors are silently swallowed.
        let perform (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch = async {
                try
                    let! r = task arg
                    dispatch (ofSuccess r)
                with _ ->
                    ()
            }

            [ bind >> AsyncHelpers.start ]

        /// Evaluate an async block and dispatch the error message on
        /// exception; success is silently swallowed.
        let attempt (task: 'a -> Async<_>) (arg: 'a) (ofError: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch = async {
                try
                    let! _ = task arg
                    ()
                with ex ->
                    dispatch (ofError ex)
            }

            [ bind >> AsyncHelpers.start ]