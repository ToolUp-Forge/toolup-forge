// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModuleEvents

open System.Collections.Generic
open ToolUp.Elmish

let private log = Logger.forCategory "client.module-events"

// ─── Cross-module client event bus ───────────────────────────────
//
// A first-class seam for one client module to broadcast a named
// event that other modules can react to — without either module
// importing the other or knowing the other exists. The shell fires
// each module's `init` / `update` once and shares that module's
// model across its pages, but modules are otherwise isolated: a
// publisher mutating its own state has no way to nudge a sibling
// module whose already-loaded model has gone stale as a result.
//
// The bus closes that gap. A module publishes by returning a `Cmd`
// from its `update` (so the publish stays MVU-pure — the side
// effect runs in the Cmd-execution phase, never in the pure update
// body); the shell subscribes to the bus once at boot and fans each
// publication out to every module that declared a matching
// subscription via `ClientModule.withEventSubscription`. Delivery
// reaches a subscriber through its own normal `update` loop — the
// subscriber maps the topic's payload into one of its own `Msg`
// values, exactly as if the user had triggered it from the UI.
//
// Routing is purely by topic string; there is no publisher identity
// on the wire. A module therefore receives its own publication only
// when it also subscribes to the same topic — the common case
// (publish to notify others) never self-delivers.
//
// Sanctioned mutable global — same precedent as
// `NavigationRequest.listeners` and `ModuleStateObserver.observers`.
// Per-tab singleton; the shell registers its subscriber at Client
// startup and disposes it on program teardown (the boot effect is a
// lifetime-aware `EffectHandle`). Subscriber exceptions are
// swallowed (with a console warning where one exists) so a buggy
// listener can never break the publishing module's Cmd execution.

/// Listener invoked for every publication. Receives the event topic
/// and its string payload. Registered by the shell at boot; consumer
/// modules never subscribe here directly — they declare subscriptions
/// declaratively via `ClientModule.withEventSubscription`, which the
/// shell collects and dispatches against the right module state.
type Listener = string -> string -> unit

let private listeners = List<Listener>()

/// Register a bus listener. Called by the shell at Client startup to
/// translate every publication into a shell `Msg` dispatch. Returns a
/// dispose thunk that removes the callback (the shell wires this into
/// an `EffectHandle` so it is torn down on program termination /
/// HMR). Re-registration of the same callback adds a second entry
/// deliberately — symmetry with `NavigationRequest.subscribe`.
let subscribe (callback: Listener) : unit -> unit =
    listeners.Add(callback)
    fun () -> listeners.Remove(callback) |> ignore

/// Internal — fire every registered listener with the topic + payload.
/// Invoked by the effect inside `publish`; not part of the consumer
/// surface. Iterates a snapshot so a listener that (un)subscribes
/// during delivery can't disturb the in-flight iteration. Swallows
/// listener exceptions so one bad subscriber can't break the rest.
let internal fire (topic: string) (payload: string) : unit =
    for cb in listeners.ToArray() do
        try
            cb topic payload
        with ex ->
            try
                log.Warn $"listener swallowed: {ex.Message}"
            with _ ->
                ()

/// Publish a named event from a module's `update`. Returns a `Cmd`
/// that fires the bus when the Elmish runtime executes it — the
/// publishing module's own dispatch is intentionally ignored, so the
/// publication never re-enters the publisher's update directly
/// (delivery reaches subscribers through the shell). Keeping publish
/// a `Cmd` rather than a direct call preserves MVU purity: the update
/// body stays a pure `Model -> Model * Cmd` transition.
///
/// `topic` is a plain string namespaced by convention (`"<app>/<event>"`
/// or bare); `payload` carries any string the subscriber needs (pass
/// `""` when the event itself is the whole signal). Topics with no
/// subscribers are a no-op — there is no registry coupling between
/// publisher and subscriber.
///
/// ```fsharp
/// // in a module's update:
/// | BatchCompleted -> { model with Pending = 0 }, ModuleEvents.publish "myapp/batch-done" ""
/// ```
let publish (topic: string) (payload: string) : Cmd<'msg> = [
    fun (_: Dispatch<'msg>) -> fire topic payload
]