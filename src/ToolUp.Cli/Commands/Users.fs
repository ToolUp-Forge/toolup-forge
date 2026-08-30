// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup users list | offboard` — the principal half of the scripted
/// tenant-lifecycle surface.
///
/// `list` reads the platform's derived principal enumeration: every user
/// the substrate has *evidence* for, aggregated from membership blobs,
/// personal storage scopes, and the sign-in audit trail. It is a
/// projection, never a stored registry, so a row cannot be stale —
/// which is what makes `--team-less` worth scripting: it is exactly the
/// set of principals with a login and no membership, the residue every
/// stray-account investigation starts from.
///
/// `offboard` is sugar. A user's personal data lives in the `user-<id>`
/// storage scope, so offboarding one is offboarding that scope — the
/// same call, the same confirmation gate, the same audit trail. It is
/// spelled separately because "offboard this user" is the sentence an
/// operator actually has, and making them derive the scope name by hand
/// is how the wrong scope gets typed into an irreversible command.
module ToolUp.Cli.Commands.Users

open System
open System.Text.Json
open ToolUp.Cli.Dispatch
open ToolUp.Cli.Commands.Tenants

/// The storage-scope name of a user's personal scope.
let userScope (userId: string) = "user-" + userId

// ── 550.B — `users list` ────────────────────────────────────────────

/// The team ids a principal holds a membership row for. Each row is a
/// `(teamId, role)` tuple, which the wire renders as a two-element array.
let private membershipTeams (principal: JsonElement) =
    arrayField principal "Memberships"
    |> List.map (fun row ->
        if row.ValueKind = JsonValueKind.Array then
            match row.EnumerateArray() |> Seq.tryHead with
            | Some teamId when teamId.ValueKind = JsonValueKind.String -> teamId.GetString()
            | Some other -> other.ToString()
            | None -> "?"
        else
            row.ToString())

/// Derived exactly as the substrate derives it: team-less **is** having
/// no membership row. The flag is a computed member server-side, so it
/// never reaches the wire — recomputing it from the same evidence is the
/// only reading that cannot disagree with the server's.
let private isTeamLess (principal: JsonElement) =
    List.isEmpty (membershipTeams principal)

let renderPrincipals (teamLessOnly: bool) (payload: JsonElement) : string list * int =
    let all =
        if payload.ValueKind = JsonValueKind.Array then
            payload.EnumerateArray() |> List.ofSeq
        else
            []

    let selected = if teamLessOnly then all |> List.filter isTeamLess else all

    if List.isEmpty selected then
        let note =
            if teamLessOnly then
                "no team-less principals"
            else
                "no principals — the substrate holds no membership, scope, or sign-in evidence"

        [ note ], ExitOk
    else
        let rows =
            selected
            |> List.map (fun p ->
                let teams = membershipTeams p

                [
                    stringField p "UserId"
                    (match teams with
                     | [] -> "-"
                     | ts -> String.concat "," ts)
                    (if List.isEmpty teams then "yes" else "no")
                    (match stringField p "LastSeenAt" with
                     | "" -> "-"
                     | seen -> seen)
                    (if boolField p "HasUserScopeData" then "yes" else "no")
                ])

        let table =
            renderTable [ "USER"; "TEAMS"; "TEAM-LESS"; "LAST-SEEN"; "SCOPE-DATA" ] rows

        let footer =
            if teamLessOnly then
                sprintf "%d team-less principal(s) of %d" (List.length selected) (List.length all)
            else
                sprintf "%d principal(s)" (List.length all)

        table @ [ ""; footer ], ExitOk

let runList (transport: Transport) (teamLessOnly: bool) : int =
    call transport (tenantRoute "ListPrincipals") "[]" (renderPrincipals teamLessOnly)

// ── 550.B — `users offboard` ────────────────────────────────────────

let runOffboardUser
    (transport: Transport)
    (userId: string)
    (reason: string)
    (exportFirst: bool)
    (token: string option)
    : int =
    runOffboard transport (userScope userId) reason exportFirst token

// ── Help + registration ─────────────────────────────────────────────

let private endpointHelp = [
    "Endpoint + credential:"
    "  --endpoint <url>    Deployment origin, e.g. https://app.example.com."
    sprintf "                      Defaults to %s." EndpointEnvVar
    sprintf "  %s   Platform-Admin bearer token (environment only)." TokenEnvVar
]

let listHelp =
    [
        "Usage: toolup users list [--team-less] [--endpoint <url>]"
        ""
        "Enumerates every principal the substrate has evidence for — membership"
        "blobs, personal `user-<id>` storage scopes, and the sign-in audit trail"
        "within its look-back window — merged per user. A derived projection, so"
        "a membership added a second ago is already reflected."
        ""
        "Options:"
        "  --team-less         Only principals holding no membership row: the"
        "                      accounts that can sign in and belong to nothing."
        ""
    ]
    @ endpointHelp

let offboardHelp =
    [
        "Usage: toolup users offboard <userId> [--export-first] [--token <t>]"
        "                             [--reason <text>] [--endpoint <url>]"
        ""
        "Offboards the user's personal storage scope (`user-<userId>`) — exactly"
        "`toolup tenants offboard user-<userId>`, with the same hooks, the same"
        "confirmation gate and the same audit trail. IRREVERSIBLE; preview it"
        "first with `toolup tenants preview user-<userId>`."
        ""
        "This offboards the user's own scope. It does NOT remove their team"
        "membership rows — a user who belongs to a team keeps that membership,"
        "and the team's data is a separate scope with its own offboard."
        ""
        "Options:"
        "  --export-first      Write the data-export archive before erasing."
        "  --token <t>         Replay a confirmation token minted by another"
        "                      admin. Never minted here."
        "  --reason <text>     Operator reason recorded in the audit trail."
        ""
    ]
    @ endpointHelp

let private withTransport (verb: string) (help: string list) (opts: Options) (body: Transport -> int) =
    match resolveEndpoint opts.Endpoint with
    | Error message -> usageError verb help message
    | Ok endpoint -> body (httpTransport endpoint)

let listCommand = {
    Path = [ "users"; "list" ]
    Summary = "List every principal the substrate has evidence for."
    Help = listHelp
    Run =
        fun args ->
            match parse defaultOptions args with
            | Error message -> usageError "users list" listHelp message
            | Ok opts when opts.ScopeId.IsSome ->
                usageError "users list" listHelp (sprintf "unexpected argument: %s" opts.ScopeId.Value)
            | Ok opts -> withTransport "users list" listHelp opts (fun t -> runList t opts.TeamLess)
}

let offboardCommand = {
    Path = [ "users"; "offboard" ]
    Summary = "Offboard a user's personal scope (IRREVERSIBLE)."
    Help = offboardHelp
    Run =
        fun args ->
            match parse defaultOptions args with
            | Error message -> usageError "users offboard" offboardHelp message
            | Ok opts when opts.TeamLess ->
                usageError "users offboard" offboardHelp "--team-less is a `users list` option"
            | Ok opts ->
                match opts.ScopeId with
                | None -> usageError "users offboard" offboardHelp "a user id is required"
                | Some userId ->
                    withTransport "users offboard" offboardHelp opts (fun t ->
                        runOffboardUser t userId opts.Reason opts.ExportFirst opts.Token)
}