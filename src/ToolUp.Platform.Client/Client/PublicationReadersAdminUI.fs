// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1107 — the publication-readers panel of the team details view.
///
/// An Owner or Admin of the active team (the publishing scope) lists, adds
/// and removes the team's in-platform publication readers, one reader role
/// at a time, choosing only among the roles the team's publications declare.
/// Self-contained (its own state, its own API proxy) so the team manager
/// only decides WHERE it renders. Every gate is the server's
/// (`IPublicationReaderApi`); the panel's own visibility rule is a courtesy.
module ToolUp.Platform.PublicationReadersAdminUI

open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

let private api: IPublicationReaderApi =
    Api.makeProxy<IPublicationReaderApi> (
        routeBuilder = PublicationReaderApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

[<ReactComponent>]
let private ReadersPanel (msgs: TeamManagerMessages) =
    let roster, setRoster = React.useState (None: PublicationReaderRoster option)
    let error, setError = React.useState (None: string option)
    let role, setRole = React.useState ""
    let userId, setUserId = React.useState ""
    let busy, setBusy = React.useState false

    let load () =
        async {
            match! api.ListReaders() with
            | Ok r ->
                setRoster (Some r)
                setError None

                if not (List.contains role r.DeclaredRoles) then
                    setRole (r.DeclaredRoles |> List.tryHead |> Option.defaultValue "")
            | Error e -> setError (Some e)
        }
        |> Async.StartImmediate

    React.useEffectOnce load

    let run (call: PublicationReaderEntry -> Async<Result<unit, string>>) (entry: PublicationReaderEntry) =
        setBusy true

        async {
            let! result = call entry
            setBusy false

            match result with
            | Ok() ->
                setUserId ""
                load ()
            | Error e -> setError (Some e)
        }
        |> Async.StartImmediate

    Layout.Panel.panel msgs.PublicationReadersPanel [
        Html.div [
            prop.className "flex flex-col gap-3"
            prop.children [
                Html.p [ prop.className "text-xs text-muted"; prop.text msgs.PublicationReadersHelp ]

                match error with
                | Some e -> Html.p [ prop.className "text-sm text-red-700"; prop.text e ]
                | None -> Html.none

                match roster with
                | None ->
                    Html.p [
                        prop.className "text-sm text-muted"
                        prop.text msgs.PublicationReadersLoading
                    ]
                | Some r when List.isEmpty r.DeclaredRoles ->
                    Html.p [
                        prop.className "text-sm text-muted"
                        prop.text msgs.PublicationReadersNoRoles
                    ]
                | Some r ->
                    if List.isEmpty r.Readers then
                        Html.p [ prop.className "text-sm text-muted"; prop.text msgs.PublicationReadersNone ]
                    else
                        Html.div [
                            for reader in r.Readers do
                                Html.div [
                                    prop.key (reader.Role + "/" + reader.UserId)
                                    prop.className "flex items-center justify-between gap-3 py-1"
                                    prop.children [
                                        Html.span [ prop.className "text-sm"; prop.text reader.UserId ]
                                        Html.span [ prop.className "text-xs text-muted"; prop.text reader.Role ]
                                        Forms.Button.secondary msgs.PublicationReaderRemove (fun () ->
                                            run api.RevokeReader reader)
                                    ]
                                ]
                        ]

                    Html.div [
                        prop.className "flex items-center gap-3 flex-wrap"
                        prop.children [
                            Html.span [
                                prop.className "text-sm text-muted"
                                prop.text msgs.PublicationReaderRoleLabel
                            ]
                            for declared in r.DeclaredRoles do
                                Html.button [
                                    prop.key declared
                                    prop.className [
                                        "px-3 py-1 rounded text-sm transition-colors border"
                                        if declared = role then
                                            "bg-brand/10 text-brand border-brand"
                                        else
                                            "bg-white text-text border-border hover:bg-gray-50"
                                    ]
                                    prop.text declared
                                    prop.onClick (fun _ -> setRole declared)
                                ]
                        ]
                    ]

                    UserDirectoryTypeahead.userTypeahead
                        userId
                        setUserId
                        msgs.PublicationReaderUserPlaceholder
                        UserDirectoryTypeahead.pickUserId

                    Html.div [
                        if busy || System.String.IsNullOrWhiteSpace userId || role = "" then
                            Html.button [
                                prop.disabled true
                                prop.className "rounded-lg px-4 py-2 text-sm opacity-50 cursor-not-allowed"
                                prop.text msgs.PublicationReaderAdd
                            ]
                        else
                            Forms.Button.primary msgs.PublicationReaderAdd (fun () ->
                                run api.GrantReader { UserId = userId.Trim(); Role = role })
                    ]
            ]
        ]
    ]

/// The panel for `teamId`, when the caller may manage it: `canManage` is the
/// caller's Owner/Admin standing on that team, and `activeTeamId` the team
/// the server's API is scoped to. On a team that is not the active one the
/// panel says so rather than showing another team's readers.
let panel (msgs: TeamManagerMessages) (teamId: string) (activeTeamId: string option) (canManage: bool) : ReactElement =
    if not canManage then
        Html.none
    elif activeTeamId <> Some teamId then
        Layout.Panel.panel msgs.PublicationReadersPanel [
            Html.p [
                prop.className "text-sm text-muted"
                prop.text msgs.PublicationReadersActiveTeamOnly
            ]
        ]
    else
        ReadersPanel msgs