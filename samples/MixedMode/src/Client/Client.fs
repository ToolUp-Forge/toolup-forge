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

// ─── Composition root ────────────────────────────────────────────
//
// The aggregate module list the SDK shell consumes. A consuming
// boot file (a stand-alone Vite + Fable host) would call
// `SDK.Client.run config modules`; this sample exposes the list
// only — the value is in the registration shape (the three
// `Visibility` predicates), not in a running mount.

let modules: ErasedModule list = [ Landing.register (); TeamDashboard.register (); SharePortal.register () ]