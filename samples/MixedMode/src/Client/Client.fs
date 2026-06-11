// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MixedMode.Client

open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 66 Stream F.5 — mixed-mode worked example, client side ─
//
// Three client modules, one per surface shape. Each declares a
// distinct `Visibility: SubjectKind -> bool` predicate so the
// shell's sidebar filter hides modules that don't apply to the
// resolved subject:
//
//   - Landing       → Visibility.visibleToAll
//   - TeamDashboard → Visibility.visibleTo [ TeamMemberKind ]
//   - SharePortal   → Visibility.visibleTo [ ClaimBearerKind ]
//
// All three are registered into the same `modules` list; the
// per-subject sidebar filtering happens at render time inside the
// SDK shell, not by the consumer. The pattern replaces the pre-66
// shell-level "Mode = Anonymous → hide everything" carve-out per
// design §3.3.

// ─── Module 1 — Landing ───────────────────────────────────────────

module private Landing =
    type Model = unit
    type Msg = NoOp

    let init () : Model * Cmd<Msg> = (), Cmd.none
    let update (_: Msg) (m: Model) : Model * Cmd<Msg> = m, Cmd.none

    let view (_: Model) (_: Msg -> unit) : ReactElement * ReactElement =
        let left =
            Html.div [
                prop.children [
                    Html.h2 [ prop.text "Landing" ]
                    Html.p [
                        prop.text "Public surface — reachable to every visitor regardless of authentication state."
                    ]
                ]
            ]

        let right = Html.div []
        left, right

    let register () : ErasedModule =
        ClientModule.create {
            Init = init
            Update = update
            Name = "Landing"
            Icon = Html.div []
        }
        |> ClientModule.withView view
        |> ClientModule.withVisibility Visibility.visibleToAll
        |> ClientModule.register

// ─── Module 2 — TeamDashboard ─────────────────────────────────────

module private TeamDashboard =
    type Model = unit
    type Msg = NoOp

    let init () : Model * Cmd<Msg> = (), Cmd.none
    let update (_: Msg) (m: Model) : Model * Cmd<Msg> = m, Cmd.none

    let view (_: Model) (_: Msg -> unit) : ReactElement * ReactElement =
        let left =
            Html.div [
                prop.children [
                    Html.h2 [ prop.text "Team Dashboard" ]
                    Html.p [
                        prop.text "Team-scoped surface — only TeamMember subjects see this module in the sidebar."
                    ]
                ]
            ]

        let right = Html.div []
        left, right

    let register () : ErasedModule =
        ClientModule.create {
            Init = init
            Update = update
            Name = "Team Dashboard"
            Icon = Html.div []
        }
        |> ClientModule.withView view
        |> ClientModule.withVisibility (Visibility.visibleTo [ TeamMemberKind ])
        |> ClientModule.register

// ─── Module 3 — SharePortal ───────────────────────────────────────

module private SharePortal =
    type Model = unit
    type Msg = NoOp

    let init () : Model * Cmd<Msg> = (), Cmd.none
    let update (_: Msg) (m: Model) : Model * Cmd<Msg> = m, Cmd.none

    let view (_: Model) (_: Msg -> unit) : ReactElement * ReactElement =
        let left =
            Html.div [
                prop.children [
                    Html.h2 [ prop.text "Share Portal" ]
                    Html.p [
                        prop.text
                            "Share-token gated surface — reachable only when the request carries a validated ShareTokenClaim."
                    ]
                ]
            ]

        let right = Html.div []
        left, right

    let register () : ErasedModule =
        ClientModule.create {
            Init = init
            Update = update
            Name = "Share Portal"
            Icon = Html.div []
        }
        |> ClientModule.withView view
        |> ClientModule.withVisibility (Visibility.visibleTo [ ClaimBearerKind ])
        |> ClientModule.register

// ─── Cross-module event bus — Notifier (publisher) ────────────────
//
// Demonstrates the `ModuleEvents` seam. `Notifier` broadcasts a
// `"mixedmode/ping"` event from its `update` whenever its button is
// clicked; it neither imports nor references the subscriber below.
// Publishing is a `Cmd` (`ModuleEvents.publish`), so the update
// stays a pure `Model -> Model * Cmd` transition — the broadcast
// runs in the Cmd-execution phase, not the update body.

module private Notifier =
    type Model = { Sent: int }
    type Msg = Ping

    let init () : Model * Cmd<Msg> = { Sent = 0 }, Cmd.none

    let update (msg: Msg) (m: Model) : Model * Cmd<Msg> =
        match msg with
        // Bump local state AND broadcast — the subscriber learns about
        // the ping through the shell, never through a direct reference.
        | Ping -> { m with Sent = m.Sent + 1 }, ModuleEvents.publish "mixedmode/ping" $"ping #{m.Sent + 1}"

    let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
        let left =
            Html.div [
                prop.children [
                    Html.h2 [ prop.text "Notifier" ]
                    Html.p [ prop.text "Publishes a cross-module event when clicked." ]
                    Html.button [ prop.text "Send ping"; prop.onClick (fun _ -> dispatch Ping) ]
                    Html.p [ prop.text $"Sent: {model.Sent}" ]
                ]
            ]

        left, Html.div []

    let register () : ErasedModule =
        ClientModule.create {
            Init = init
            Update = update
            Name = "Notifier"
            Icon = Html.div []
        }
        |> ClientModule.withView view
        |> ClientModule.register

// ─── Cross-module event bus — Listener (subscriber) ───────────────
//
// Subscribes to `"mixedmode/ping"` via `withEventSubscription`. The
// shell maps each published payload into this module's `Received`
// `Msg` and runs it through the module's own `update` — even when
// `Listener` is not the active sidebar page, so its model is fresh
// the next time the user navigates to it. No reference to `Notifier`.

module private Listener =
    type Model = { Count: int; Last: string }
    type Msg = Received of string

    let init () : Model * Cmd<Msg> = { Count = 0; Last = "" }, Cmd.none

    let update (msg: Msg) (m: Model) : Model * Cmd<Msg> =
        match msg with
        | Received payload ->
            {
                m with
                    Count = m.Count + 1
                    Last = payload
            },
            Cmd.none

    let view (model: Model) (_: Msg -> unit) : ReactElement * ReactElement =
        let left =
            Html.div [
                prop.children [
                    Html.h2 [ prop.text "Listener" ]
                    Html.p [ prop.text "Reacts to events published by sibling modules." ]
                    Html.p [ prop.text $"Received: {model.Count}" ]
                    Html.p [ prop.text $"Last: {model.Last}" ]
                ]
            ]

        left, Html.div []

    let register () : ErasedModule =
        ClientModule.create {
            Init = init
            Update = update
            Name = "Listener"
            Icon = Html.div []
        }
        |> ClientModule.withView view
        |> ClientModule.withEventSubscription "mixedmode/ping" Received
        |> ClientModule.register

// ─── Composition root ────────────────────────────────────────────
//
// The aggregate module list the SDK shell consumes. A consuming
// boot file (a stand-alone Vite + Fable host) would call
// `SDK.Client.run config modules`; this sample exposes the list
// only — the value is in the registration shape (the three
// `Visibility` predicates + the `Notifier`/`Listener` event-bus
// pair), not in a running mount.

let modules: ErasedModule list = [
    Landing.register ()
    TeamDashboard.register ()
    SharePortal.register ()
    Notifier.register ()
    Listener.register ()
]