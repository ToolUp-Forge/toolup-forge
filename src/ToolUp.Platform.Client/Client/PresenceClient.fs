// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PresenceClient

open Fable.Core
open ToolUp.Platform

// ─── Presence client hook — Phase 241 (client tier) ──────────────────
//
// The Fable/Elmish client half of the presence substrate: an
// interval heartbeat + a live scope roster the view renders. It is
// deliberately *transport-parameterised* — the consumer supplies how a
// heartbeat / roster read reaches the server (typically a ToolUp.Remoting
// API backed by the server-side `IPresenceChannel`), so this hook stays
// neutral about the wire and composes with any presence endpoint.
//
// Opt-in (GP 13): nothing runs until a view starts the subscription.

[<Emit("setInterval($0, $1)")>]
let private setInterval (cb: unit -> unit) (ms: int) : int = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: int) : unit = jsNative

/// How the hook reaches the server. Wire each to the deployment's
/// presence endpoint (e.g. a Remoting API over `IPresenceChannel`).
type PresenceTransport = {
    /// Refresh this principal's liveness for the current scope.
    Heartbeat: unit -> Async<unit>
    /// Read the current scope roster.
    FetchRoster: unit -> Async<PresenceEntry list>
    /// Announce departure (best-effort; invoked on dispose).
    Leave: unit -> Async<unit>
}

type Model = { Roster: PresenceEntry list }

type Msg = RosterUpdated of PresenceEntry list

let init () : Model = { Roster = [] }

let update (msg: Msg) (model: Model) : Model =
    match msg with
    | RosterUpdated roster -> { model with Roster = roster }

/// Start the heartbeat + roster-poll loop. Fires immediately, then every
/// `intervalMs`. Returns a dispose thunk (clears the timer + sends a
/// best-effort `Leave`) — call it from the owning component's teardown.
/// Matching the codebase's raw `setInterval`/`clearInterval` subscription
/// shape (e.g. `UserSession`).
let start (transport: PresenceTransport) (intervalMs: int) (dispatch: Msg -> unit) : unit -> unit =
    let tick () =
        Async.StartImmediate(
            async {
                do! transport.Heartbeat()
                let! roster = transport.FetchRoster()
                dispatch (RosterUpdated roster)
            }
        )

    tick () // immediate first beat so the roster populates without waiting a full interval
    let handle = setInterval tick intervalMs

    fun () ->
        clearInterval handle
        Async.StartImmediate(transport.Leave())

/// Online members of the roster, sorted for stable rendering — the
/// "who's here right now" list a view binds to.
let onlineCollaborators (model: Model) : PresenceEntry list =
    model.Roster
    |> List.filter (fun e -> e.Status = Online)
    |> List.sortBy _.PrincipalId